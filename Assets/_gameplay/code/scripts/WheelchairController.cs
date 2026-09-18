using UnityEngine;

namespace Delirium.Wheelchair
{
    /// <summary>
    /// Chair root. Each frame: hands read input, rims update their spin, then the chair moves
    /// from the two wheels' ground speeds - forward is their average, turn is their difference,
    /// rotating about the midpoint between the hubs. Moves through a CharacterController, and
    /// when something blocks the chair the wheels lose speed to match.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class WheelchairController : MonoBehaviour
    {
        [Header("Parts")]
        [SerializeField] PushRim leftRim;
        [SerializeField] PushRim rightRim;
        [SerializeField] RimHand[] hands;

        [Tooltip("Optional powered joystick. Leave empty for a chair without one.")]
        [SerializeField] ChairJoystick joystick;

        [Header("Hold to keep moving")]
        [Tooltip("When on, pushing the rim and then holding it there keeps the chair rolling. When off, a held still hand brakes the wheel like a real rim. Cruise feel is tuned per wheel on PushRim.")]
        [SerializeField] bool holdToKeepMoving = true;

        [Header("Straight-line assist")]
        [Tooltip("When both wheels are doing the same thing, ground speeds closer than this (m/s) get pulled together, hiding differences between the hands. 0 disables.")]
        [SerializeField] float straightAssistWindow = 0.35f;

        [Tooltip("How fast the assist pulls the wheels together (1/s).")]
        [SerializeField] float straightAssistRate = 12f;

        [Tooltip("Also pull the wheels together while both are coasting, so the chair doesn't curve away after a two-handed push.")]
        [SerializeField] bool assistWhileCoasting = true;

        [Tooltip("Turn rates below this (deg/s) are ignored while both wheels run the same way, so a slightly uneven push doesn't drift the chair off course. 0 disables.")]
        [SerializeField] float headingDeadzone = 5f;

        [Tooltip("A wheel slower than this (m/s) counts as standing still, so neither the assist nor the deadzone touches a deliberate one-wheel turn.")]
        [SerializeField] float stillWheelSpeed = 0.05f;

        [Header("Collision")]
        [Tooltip("How much wheel speed is cut when the chair is blocked (0 = wheels ignore walls, 1 = wheels stop with the chair).")]
        [Range(0f, 1f)] [SerializeField] float blockedWheelDamping = 1f;

        [Header("Gravity")]
        [SerializeField] float gravity = 9.81f;

        [Header("Debug")]
        [Tooltip("The player's head, so the gizmos can show where it sits relative to the axle.")]
        [SerializeField] Transform head;

        [Tooltip("Draw where the chair is actually turning around (the instantaneous centre).")]
        [SerializeField] bool drawTurningCentre = false;

        [Tooltip("Draw the gap between the head and the axle.")]
        [SerializeField] bool drawHeadOffset = false;

        [Tooltip("Draw a trail of where the chair has been.")]
        [SerializeField] bool drawTrail = false;

        [Tooltip("Scene-view text above the chair: speed, turn rate and blocking.")]
        [SerializeField] bool drawLabels = false;

        [Tooltip("Seconds of trail to keep.")]
        [SerializeField] float trailSeconds = 6f;

        [Tooltip("Log chair speed, turn rate and blocking a few times a second while moving.")]
        [SerializeField] bool logMotion = true;

        [Tooltip("While one wheel turns and the other stands still, measure how far the still wheel actually drifts. If the maths is right this stays near zero.")]
        [SerializeField] bool logPivotDrift = true;

        [Tooltip("Seconds between motion log lines.")]
        [SerializeField] float motionLogInterval = 0.5f;

        CharacterController characterController;
        float verticalVelocity;
        bool isValid;
        float nextMotionLogTime;
        float lastBlockRatio = 1f;
        readonly System.Collections.Generic.Queue<Vector3> trail = new System.Collections.Generic.Queue<Vector3>();
        float nextTrailSample;
        Vector3 previousLeftHub, previousRightHub;
        bool hasPreviousHubs;
        float nextDriftLogTime;

        public bool HoldToKeepMoving
        {
            get => holdToKeepMoving;
            set => holdToKeepMoving = value;
        }

        public float ForwardSpeed { get; private set; }
        public float YawRate { get; private set; }

        void Awake()
        {
            characterController = GetComponent<CharacterController>();

            // The default min move distance silently drops slow per-frame moves, which would stall
            // creeping and make the blocked check think the chair hit something.
            characterController.minMoveDistance = 0f;

            isValid = Validate();
            if (!isValid)
            {
                Debug.LogError("[WheelchairController] Setup is incomplete (see errors above). The chair is disabled until it's fixed.", this);
                return;
            }

            leftRim.Bind(transform);
            rightRim.Bind(transform);
            if (joystick != null) joystick.Bind(transform);

            float wheelbase = Mathf.Abs(Vector3.Dot(rightRim.HubPosition - leftRim.HubPosition, transform.right));
            Debug.Log($"[WheelchairController] Ready. Wheelbase {wheelbase:0.000} m, {hands.Length} hand(s), hold to keep moving {(holdToKeepMoving ? "ON" : "OFF")}.", this);

            if (Vector3.Dot(rightRim.HubPosition - leftRim.HubPosition, transform.right) < 0f)
                Debug.LogWarning("[WheelchairController] Left Rim is to the RIGHT of Right Rim along the chair's X axis. The rims are swapped, or the chair root is facing backwards - turning will be inverted.", this);
        }

        bool Validate()
        {
            bool ok = true;

            if (leftRim == null) { Debug.LogError("[WheelchairController] Left Rim is not assigned.", this); ok = false; }
            if (rightRim == null) { Debug.LogError("[WheelchairController] Right Rim is not assigned.", this); ok = false; }
            if (leftRim != null && leftRim == rightRim) { Debug.LogError("[WheelchairController] Left Rim and Right Rim are the same object.", this); ok = false; }

            if (hands == null || hands.Length == 0)
            {
                Debug.LogError("[WheelchairController] The Hands list is empty - no hand will ever grab a rim.", this);
                ok = false;
            }
            else
            {
                for (int i = 0; i < hands.Length; i++)
                    if (hands[i] == null)
                        Debug.LogWarning($"[WheelchairController] Hands element {i} is empty.", this);
            }

            return ok;
        }

        void Update()
        {
            if (!isValid) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            foreach (RimHand hand in hands)
                if (hand != null && hand.isActiveAndEnabled)
                    hand.Tick();

            if (joystick != null) joystick.Tick(dt, hands);

            leftRim.Tick(dt, holdToKeepMoving);
            rightRim.Tick(dt, holdToKeepMoving);

            ApplyJoystick();

            ApplyStraightAssist(dt);

            float leftSpeed = leftRim.GroundSpeed;
            float rightSpeed = rightRim.GroundSpeed;

            Vector3 leftHub = leftRim.HubPosition;
            Vector3 rightHub = rightRim.HubPosition;
            float wheelbase = Mathf.Max(Mathf.Abs(Vector3.Dot(rightHub - leftHub, transform.right)), 0.01f);

            float forwardSpeed = (leftSpeed + rightSpeed) * 0.5f;

            // Unity's positive yaw turns right, which is what a faster left wheel does.
            float yawRate = (leftSpeed - rightSpeed) / wheelbase;

            // A push that's a few percent uneven shouldn't steer the chair. Deliberate turns, where
            // the wheels run at clearly different speeds or opposite ways, pass straight through.
            if (headingDeadzone > 0f && Mathf.Abs(yawRate) * Mathf.Rad2Deg < headingDeadzone
                && BothRunningTogether(leftSpeed, rightSpeed))
                yawRate = 0f;

            // Rotate about the axle midpoint, not the root, so a spin in place stays in place
            // and a one-wheel push pivots on the still wheel.
            Vector3 pivot = (leftHub + rightHub) * 0.5f;
            Quaternion turn = Quaternion.AngleAxis(yawRate * Mathf.Rad2Deg * dt, Vector3.up);
            Vector3 pivotCorrection = (pivot + turn * (transform.position - pivot)) - transform.position;

            Quaternion newRotation = turn * transform.rotation;
            Vector3 newForward = Vector3.ProjectOnPlane(newRotation * Vector3.forward, Vector3.up).normalized;

            Vector3 planarMove = pivotCorrection + newForward * forwardSpeed * dt;

            if (characterController.isGrounded && verticalVelocity < 0f)
                verticalVelocity = -1f;
            else
                verticalVelocity -= gravity * dt;

            Vector3 before = transform.position;
            transform.rotation = newRotation;
            characterController.Move(planarMove + Vector3.up * verticalVelocity * dt);

            ApplyBlockedFeedback(transform.position - before, planarMove, newForward);

            ForwardSpeed = (leftRim.GroundSpeed + rightRim.GroundSpeed) * 0.5f;
            YawRate = yawRate;

            LogMotion(leftSpeed, rightSpeed, forwardSpeed, yawRate);
            LogPivotDrift(dt);
            SampleTrail();
        }

        /// <summary>
        /// The joystick drives the wheels rather than the chair, so the tyres keep matching the
        /// ground and a hand can still grab a rim to override or brake whatever it's doing.
        /// </summary>
        void ApplyJoystick()
        {
            if (joystick == null || !joystick.IsDriving) return;

            float wheelbase = Mathf.Max(Mathf.Abs(Vector3.Dot(rightRim.HubPosition - leftRim.HubPosition, transform.right)), 0.01f);
            joystick.GetWheelSpeeds(wheelbase, out float left, out float right);

            if (!leftRim.IsGripped) leftRim.SetGroundSpeed(left);
            if (!rightRim.IsGripped) rightRim.SetGroundSpeed(right);
        }

        void ApplyStraightAssist(float dt)
        {
            if (straightAssistWindow <= 0f) return;

            bool bothGripped = leftRim.IsGripped && rightRim.IsGripped;
            bool bothFree = !leftRim.IsGripped && !rightRim.IsGripped;
            if (!bothGripped && !(assistWhileCoasting && bothFree)) return;

            float left = leftRim.GroundSpeed;
            float right = rightRim.GroundSpeed;
            // A wheel that's standing still is a pivot, not an uneven push. Pulling it up to match
            // the other one would turn every one-wheel turn into a drift forward.
            if (!BothRunningTogether(left, right)) return;

            float difference = Mathf.Abs(left - right);
            if (difference >= straightAssistWindow) return;

            // Full strength when the speeds nearly match, fading out toward the edge of the window,
            // so a deliberate turn is never flattened.
            float weight = (1f - difference / straightAssistWindow) * (1f - Mathf.Exp(-straightAssistRate * dt));
            float average = (left + right) * 0.5f;

            leftRim.SetGroundSpeed(Mathf.Lerp(left, average, weight));
            rightRim.SetGroundSpeed(Mathf.Lerp(right, average, weight));
        }

        /// <summary>True only when both wheels are actually turning, the same way.</summary>
        bool BothRunningTogether(float left, float right)
        {
            if (Mathf.Abs(left) < stillWheelSpeed || Mathf.Abs(right) < stillWheelSpeed) return false;
            return Mathf.Sign(left) == Mathf.Sign(right);
        }

        void ApplyBlockedFeedback(Vector3 actualMove, Vector3 intendedMove, Vector3 forward)
        {
            lastBlockRatio = 1f;
            if (blockedWheelDamping <= 0f) return;

            float intendedForward = Vector3.Dot(intendedMove, forward);
            if (Mathf.Abs(intendedForward) < 1e-5f) return;

            float actualForward = Vector3.Dot(actualMove, forward);
            float ratio = Mathf.Clamp01(actualForward / intendedForward);
            if (ratio >= 0.999f) return;

            lastBlockRatio = ratio;
            float factor = Mathf.Lerp(1f, ratio, blockedWheelDamping);
            leftRim.ScaleSpeed(factor);
            rightRim.ScaleSpeed(factor);
        }

        // ---------------------------------------------------------------- debug

        void SampleTrail()
        {
            if (!drawTrail) { trail.Clear(); return; }
            if (Time.time < nextTrailSample) return;

            nextTrailSample = Time.time + 0.05f;
            trail.Enqueue(transform.position);
            while (trail.Count > Mathf.Max(2, Mathf.RoundToInt(trailSeconds / 0.05f)))
                trail.Dequeue();
        }

        /// <summary>
        /// Measures the wheels rather than trusting the maths: when one wheel is turning and the
        /// other isn't, the still one should barely move. Anything else means the chair is pivoting
        /// somewhere it shouldn't be.
        /// </summary>
        void LogPivotDrift(float dt)
        {
            Vector3 leftHub = leftRim.HubPosition;
            Vector3 rightHub = rightRim.HubPosition;

            if (!hasPreviousHubs)
            {
                previousLeftHub = leftHub;
                previousRightHub = rightHub;
                hasPreviousHubs = true;
                return;
            }

            Vector3 leftStep = leftHub - previousLeftHub;
            Vector3 rightStep = rightHub - previousRightHub;
            previousLeftHub = leftHub;
            previousRightHub = rightHub;

            if (!logPivotDrift || dt <= 0f || Time.time < nextDriftLogTime) return;

            float left = leftRim.GroundSpeed;
            float right = rightRim.GroundSpeed;
            bool leftTurning = Mathf.Abs(left) >= stillWheelSpeed;
            bool rightTurning = Mathf.Abs(right) >= stillWheelSpeed;
            if (leftTurning == rightTurning) return;

            nextDriftLogTime = Time.time + 0.25f;

            string stillName = leftTurning ? rightRim.name : leftRim.name;
            Vector3 stillStep = leftTurning ? rightStep : leftStep;
            float drift = new Vector3(stillStep.x, 0f, stillStep.z).magnitude / dt;
            float drivenSpeed = leftTurning ? left : right;

            Debug.Log($"[WheelchairController] pivot check: {stillName} is standing still, driven wheel {drivenSpeed:0.00} m/s. " +
                      $"The still hub is moving {drift:0.000} m/s ({drift / Mathf.Max(Mathf.Abs(drivenSpeed), 0.001f):P0} of the driven wheel). " +
                      "Near zero means the chair really is pivoting on it.", this);
        }

        void LogMotion(float leftSpeed, float rightSpeed, float forwardSpeed, float yawRate)
        {
            if (!logMotion || Time.time < nextMotionLogTime) return;
            if (Mathf.Abs(leftSpeed) < 0.005f && Mathf.Abs(rightSpeed) < 0.005f) return;

            nextMotionLogTime = Time.time + motionLogInterval;
            Debug.Log($"[WheelchairController] L {leftSpeed:0.00} m/s, R {rightSpeed:0.00} m/s -> forward {forwardSpeed:0.00} m/s, " +
                      $"turn {yawRate * Mathf.Rad2Deg:0} deg/s, grounded {characterController.isGrounded}" +
                      (lastBlockRatio < 0.999f ? $", BLOCKED (moved {lastBlockRatio:P0} of intended)" : ""), this);
        }

        void OnDrawGizmos()
        {
            if (leftRim == null || rightRim == null) return;

            Vector3 leftHub = leftRim.HubPosition;
            Vector3 rightHub = rightRim.HubPosition;
            Vector3 pivot = (leftHub + rightHub) * 0.5f;

            // Axle line and turning pivot.
            Gizmos.color = Color.white;
            Gizmos.DrawLine(leftHub, rightHub);
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireSphere(pivot, 0.03f);

            // Chair forward.
            Gizmos.color = Color.blue;
            Vector3 flatForward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            Gizmos.DrawLine(pivot, pivot + flatForward * 0.4f);

            // Where the head sits relative to the axle: the gap that makes turns feel off-centre.
            if (drawHeadOffset && head != null)
            {
                Vector3 headOnGround = new Vector3(head.position.x, pivot.y, head.position.z);
                float forwardOffset = Vector3.Dot(headOnGround - pivot, flatForward);
                Vector3 axleFoot = headOnGround - flatForward * forwardOffset;

                Gizmos.color = Mathf.Abs(forwardOffset) < 0.05f ? Color.green : new Color(1f, 0.5f, 0f);
                Gizmos.DrawLine(head.position, headOnGround);
                Gizmos.DrawLine(headOnGround, axleFoot);
                Gizmos.DrawWireSphere(headOnGround, 0.04f);
#if UNITY_EDITOR
                if (drawLabels)
                    UnityEditor.Handles.Label((headOnGround + axleFoot) * 0.5f + Vector3.up * 0.05f,
                    $"head {forwardOffset * 100f:0} cm {(forwardOffset >= 0f ? "ahead of" : "behind")} axle");
#endif
            }

            if (!Application.isPlaying) return;

            // Actual velocity.
            Gizmos.color = lastBlockRatio < 0.999f ? Color.red : Color.cyan;
            Gizmos.DrawLine(pivot + Vector3.up * 0.02f, pivot + Vector3.up * 0.02f + flatForward * ForwardSpeed * 0.5f);

            // Where the chair is really turning around. Sits on the still wheel during a one-wheel
            // push, on the axle midpoint during a spin in place, and far out on gentle arcs.
            if (drawTurningCentre && Mathf.Abs(YawRate) > 0.01f)
            {
                float radius = ForwardSpeed / YawRate;
                if (Mathf.Abs(radius) < 25f)
                {
                    Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward).normalized * -1f;
                    Vector3 centre = pivot - flatRight * radius;

                    Gizmos.color = Color.yellow;
                    Gizmos.DrawWireSphere(centre, 0.05f);
                    Gizmos.DrawLine(centre, centre + Vector3.up * 0.4f);
                    Gizmos.DrawLine(pivot, centre);
                    DrawCircle(centre, Mathf.Abs(radius), 48);
#if UNITY_EDITOR
                    if (drawLabels)
                        UnityEditor.Handles.Label(centre + Vector3.up * 0.45f, $"turning centre\nradius {radius:0.00} m");
#endif
                }
            }

            // Where the chair has been.
            if (drawTrail && trail.Count > 1)
            {
                Gizmos.color = new Color(0f, 1f, 1f, 0.5f);
                Vector3[] points = trail.ToArray();
                for (int i = 1; i < points.Length; i++)
                    Gizmos.DrawLine(points[i - 1], points[i]);
            }

#if UNITY_EDITOR
            if (drawLabels)
                UnityEditor.Handles.Label(pivot + Vector3.up * 0.5f,
                $"chair {ForwardSpeed:0.00} m/s\nturn {YawRate * Mathf.Rad2Deg:0} deg/s" +
                (lastBlockRatio < 0.999f ? $"\nBLOCKED {lastBlockRatio:P0}" : ""));
#endif
        }

        static void DrawCircle(Vector3 centre, float radius, int segments)
        {
            Vector3 previous = centre + Vector3.right * radius;
            for (int i = 1; i <= segments; i++)
            {
                float angle = i / (float)segments * Mathf.PI * 2f;
                Vector3 next = centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                Gizmos.DrawLine(previous, next);
                previous = next;
            }
        }
    }
}
