using UnityEngine;

public class PlayerMovement : MonoBehaviour
{
    // Debug view of current wheel input
    [SerializeField] Vector3 wh_input;

    // Smoothed wheel velocities
    float wheel_velocity_l = 0, wheel_velocity_r = 0;

    // Anchor points representing the wheel contact points
    [SerializeField] Transform anchor_L, anchor_R, mid, controller;

    // Movement speed multiplier
    [SerializeField] float speed = 0.5f;

    // How quickly the velocity changes (higher = snappier, lower = smoother)
    [SerializeField] float inputSmooth = 10f;

    // --- Wheel sync ---
    // If both wheels are being pushed by roughly the same amount, they get
    // silently pulled toward their average so tiny asymmetries between your
    // two hands don't produce unintended turning during straight-line pushes.
    // Genuine differential pushes (turning) stay untouched.
    [SerializeField]
    [Tooltip("Difference between wheel targets below which they are fully synced (averaged)")]
    float syncDeadzone = 0.5f;

    [SerializeField]
    [Tooltip("Additional difference range over which syncing fades out - beyond deadzone+range, wheels are fully independent")]
    float syncRange = 0.5f;

    [SerializeField]
    [Tooltip("How quickly the sync amount reacts to changing input. Lower = more tolerance for brief async before it commits to syncing (matches 'allow async, sync silently')")]
    float syncSmooth = 7.5f;

    // Current smoothed confidence (0-1) that the wheels should be synced.
    // Not reset to zero between frames - it decays/rises continuously.
    float wheel_sync_amount = 0f;

    // Latest target velocity reported by each knob's onValueChange event.
    // These persist between frames (they are NOT reset to zero after being
    // read) so a knob event that arrives a frame late, or out of sync with
    // the other wheel's event, doesn't yank that wheel's target back to
    // zero. The smoothing below still does the rest.
    float target_velocity_l = 0, target_velocity_r = 0;

    float wheelbase;

    void Start()
    {
        wheelbase = Vector3.Distance(anchor_L.position, anchor_R.position);
    }

    void Update()
    {
        // Get smoothed wheelchair input
        wh_input = GetChairInput();

        // Forward speed = average of both wheels; turn speed = their difference
        float forwardSpeed = (wh_input.x + wh_input.y) * 0.5f * speed;
        float turnSpeed = (wh_input.y - wh_input.x) * speed / wheelbase;

        transform.position += transform.forward * forwardSpeed * Time.deltaTime;
        transform.rotation *= Quaternion.Euler(0f, turnSpeed * Mathf.Rad2Deg * Time.deltaTime, 0f);
    }

    Vector3 GetChairInput()
    {
        // --- Sync step ---
        // How far apart are the two wheels' raw targets right now?
        float diff = Mathf.Abs(target_velocity_l - target_velocity_r);

        // 1 = fully sync (average them), 0 = fully independent (leave as-is)
        float rawSyncAmount = 1f - Mathf.Clamp01((diff - syncDeadzone) / Mathf.Max(syncRange, 0.0001f));

        // Smooth the sync confidence itself, separately from the velocity smoothing
        // below - this is what lets brief async survive instead of being erased
        // the instant the difference dips under the deadzone for one frame.
        float syncSmoothFactor = 1f - Mathf.Exp(-syncSmooth * Time.deltaTime);
        wheel_sync_amount = Mathf.Lerp(wheel_sync_amount, rawSyncAmount, syncSmoothFactor);

        float average = (target_velocity_l + target_velocity_r) * 0.5f;
        float syncedTargetL = Mathf.Lerp(target_velocity_l, average, wheel_sync_amount);
        float syncedTargetR = Mathf.Lerp(target_velocity_r, average, wheel_sync_amount);

        // Exponential-decay smoothing instead of Mathf.Lerp(a, b, rate*dt).
        // The Lerp version is only an approximation of true framerate-independent
        // smoothing - under VR frame-time variance (dropped frames, reprojection,
        // load spikes) it doesn't decay by a consistent proportion per second,
        // so the "amount" of smoothing subtly shifts frame to frame. This formula
        // (1 - e^-rate*dt) gives the same style of smoothing but converges at a
        // truly constant rate regardless of framerate, so inputSmooth means the
        // same thing at 45fps as it does at 90fps.
        float smoothFactor = 1f - Mathf.Exp(-inputSmooth * Time.deltaTime);
        wheel_velocity_l = Mathf.Lerp(wheel_velocity_l, syncedTargetL, smoothFactor);
        wheel_velocity_r = Mathf.Lerp(wheel_velocity_r, syncedTargetR, smoothFactor);

        // Return smoothed input vector
        return new Vector3(wheel_velocity_l, wheel_velocity_r, 0);
    }

    public void LeftWheelVelocity(float velocity)
    {
        target_velocity_r = (velocity - 0.5f) * -2f;
    }
    public void RightWheelVelocity(float velocity)
    {
        target_velocity_l = (velocity - 0.5f) * -2f;
    }
}