using System;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Haptics;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

namespace UnityEngine.XR.Content.Interaction
{
    /// <summary>
    /// An interactable knob that follows the rotation of the interactor
    /// </summary>
    public class XRKnob : XRBaseInteractable
    {
        const float k_ModeSwitchDeadZone = 0.1f; // Prevents rapid switching between the different rotation tracking modes

        /// <summary>
        /// Helper class used to track rotations that can go beyond 180 degrees while minimizing accumulation error
        /// </summary>
        struct TrackedRotation
        {
            /// <summary>
            /// The anchor rotation we calculate an offset from
            /// </summary>
            float m_BaseAngle;

            /// <summary>
            /// The target rotate we calculate the offset to
            /// </summary>
            float m_CurrentOffset;

            /// <summary>
            /// Any previous offsets we've added in
            /// </summary>
            float m_AccumulatedAngle;

            /// <summary>
            /// The total rotation that occurred from when this rotation started being tracked
            /// </summary>
            public float totalOffset => m_AccumulatedAngle + m_CurrentOffset;

            /// <summary>
            /// Resets the tracked rotation so that total offset returns 0
            /// </summary>
            public void Reset()
            {
                m_BaseAngle = 0.0f;
                m_CurrentOffset = 0.0f;
                m_AccumulatedAngle = 0.0f;
            }

            /// <summary>
            /// Sets a new anchor rotation while maintaining any previously accumulated offset
            /// </summary>
            /// <param name="direction">The XZ vector used to calculate a rotation angle</param>
            public void SetBaseFromVector(Vector3 direction)
            {
                // Update any accumulated angle
                m_AccumulatedAngle += m_CurrentOffset;

                // Now set a new base angle
                m_BaseAngle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;
                m_CurrentOffset = 0.0f;
            }

            public void SetTargetFromVector(Vector3 direction)
            {
                // Set the target angle
                var targetAngle = Mathf.Atan2(direction.z, direction.x) * Mathf.Rad2Deg;

                // Return the offset
                m_CurrentOffset = ShortestAngleDistance(m_BaseAngle, targetAngle, 360.0f);

                // If the offset is greater than 90 degrees, we update the base so we can rotate beyond 180 degrees
                if (Mathf.Abs(m_CurrentOffset) > 90.0f)
                {
                    m_BaseAngle = targetAngle;
                    m_AccumulatedAngle += m_CurrentOffset;
                    m_CurrentOffset = 0.0f;
                }
            }
        }

        [Serializable]
        public class ValueChangeEvent : UnityEvent<float> { }

        [SerializeField]
        [Tooltip("The object that is visually grabbed and manipulated")]
        Transform m_Handle = null;

        [SerializeField]
        [Tooltip("The value of the knob")]
        [Range(0.0f, 1.0f)]
        float m_Value = 0.5f;

        [SerializeField]
        [Tooltip("Whether this knob's rotation should be clamped by the angle limits")]
        bool m_ClampedMotion = true;

        [SerializeField]
        [Tooltip("When enabled, setting 'value' directly in code (e.g. via a UnityEvent on Select Exited) will NOT snap the handle's visual rotation to match. The handle keeps whatever rotation it last had; only the underlying value resets. While actively grabbed, the handle spins continuously at a rate driven by how far it's twisted from the grab start (hold it turned = constant spin speed), while 'value' still reflects that twist offset directly. Releasing stops the spin immediately, unless Use Momentum is also enabled. See also Hold To Maintain Spin.")]
        bool m_DecoupleVisualFromValue = false;

        [SerializeField]
        [Tooltip("Only used when Decouple Visual From Value is enabled. When ON (default), holding the controller twisted at a constant offset keeps the handle spinning at a constant rate for as long as it's held - the position isn't changing, but the spin is maintained. When OFF, the spin instead only reflects the controller's actual frame-to-frame movement, so if the controller stops moving - even while still held twisted away from the grab start - the spin stops too, since the position isn't changing.")]
        bool m_HoldToMaintainSpin = true;

        [SerializeField]
        [Tooltip("Rotation of the knob at value '1'")]
        float m_MaxAngle = 90.0f;

        [SerializeField]
        [Tooltip("Rotation of the knob at value '0'")]
        float m_MinAngle = -90.0f;

        [SerializeField]
        [Tooltip("Angle increments to support, if greater than '0'")]
        float m_AngleIncrement = 0.0f;

        [SerializeField]
        [Tooltip("The position of the interactor controls rotation when outside this radius")]
        float m_PositionTrackedRadius = 0.1f;

        [SerializeField]
        [Tooltip("When enabled, if the interactor moves too far from the handle while dragging, the grab is force-released (as if the trigger/grip were let go), instead of continuing to track rotation from far away.")]
        bool m_ReleaseOnDragOutOfRange = false;

        [SerializeField]
        [Tooltip("Distance from the handle beyond which an active drag is force-released. Only used when Release On Drag Out Of Range is enabled.")]
        float m_DragReleaseDistance = 0.5f;

        [SerializeField]
        [Tooltip("How much controller rotation ")]
        float m_TwistSensitivity = 1.5f;

        [SerializeField]
        [Tooltip("When enabled (and Decouple Visual From Value is on), releasing the wheel while it's spinning makes it coast at its last spin rate and decelerate via friction to a stop, instead of halting immediately.")]
        bool m_UseMomentum = false;

        [SerializeField]
        [Tooltip("How quickly momentum decays after release, in degrees/sec^2. Higher values stop the wheel sooner. Only used when Use Momentum is enabled.")]
        float m_Friction = 180.0f;

        [SerializeField]
        [Tooltip("Whether this knob sends haptic pulses at all (touch, grab, and continuous spin)")]
        bool m_HapticsEnabled = true;

        [SerializeField]
        [Tooltip("Pulse strength for a brief tap when the controller first touches/hovers the knob")]
        float m_TouchHapticAmplitude = 0.15f;

        [SerializeField]
        [Tooltip("Duration in seconds of the touch pulse")]
        float m_TouchHapticDuration = 0.05f;

        [SerializeField]
        [Tooltip("Pulse strength for a brief tap when the controller stops touching/hovering the knob (hand removed)")]
        float m_UntouchHapticAmplitude = 0.1f;

        [SerializeField]
        [Tooltip("Duration in seconds of the untouch pulse")]
        float m_UntouchHapticDuration = 0.05f;

        [SerializeField]
        [Tooltip("Pulse strength for the moment the knob is grabbed")]
        float m_GrabHapticAmplitude = 0.4f;

        [SerializeField]
        [Tooltip("Duration in seconds of the grab pulse")]
        float m_GrabHapticDuration = 0.08f;

        [SerializeField]
        [Tooltip("Continuous haptic strength (0-1) once spin speed reaches Spin Haptic Full Speed Threshold, while the wheel is actively being turned")]
        float m_SpinHapticAmplitude = 0.3f;

        [SerializeField]
        [Tooltip("Angular speed (degrees/sec) at which the continuous spin haptic reaches full Spin Haptic Amplitude. Spin speeds above this clamp rather than getting stronger.")]
        float m_SpinHapticFullSpeedThreshold = 360.0f;

        [SerializeField]
        [Tooltip("Events to trigger when the knob is rotated")]
        ValueChangeEvent m_OnValueChange = new ValueChangeEvent();

        IXRSelectInteractor m_Interactor;

        bool m_PositionDriven = false;
        bool m_UpVectorDriven = false;

        TrackedRotation m_PositionAngles = new TrackedRotation();
        TrackedRotation m_UpVectorAngles = new TrackedRotation();
        TrackedRotation m_ForwardVectorAngles = new TrackedRotation();

        float m_BaseKnobRotation = 0.0f;

        // While decoupled and grabbed, the mesh doesn't track a position at all - it spins
        // continuously at a rate driven by how far the controller is twisted from the grab
        // start. This accumulates that spin every frame. It intentionally is not wrapped to
        // 0-360 so the rate math stays continuous; SetKnobRotation() handles display wrapping.
        float m_VisualSpinAngle = 0.0f;

        // Kept in sync with the live twist offset while grabbed. On release, if momentum is
        // enabled, this becomes the coasting spin rate and decays toward zero via friction
        // instead of being zeroed out immediately.
        float m_MomentumOffset = 0.0f;

        // True while the wheel is coasting on momentum after release (ungrabbed).
        bool m_IsCoasting = false;

        // The "at rest" value for decoupled/momentum wheels, captured once at startup and never
        // touched again. Momentum and live decoupled input both express value as an offset from
        // this fixed point, so releasing always settles back to the same rest value - it can't
        // drift over repeated grab/release cycles the way re-deriving it from m_Value each grab
        // would (e.g. if you grab again before a previous coast has fully settled).
        float m_RestValue = 0.5f;

        // The raw knob rotation from the previous frame, used purely to derive an angular
        // speed (degrees/sec) for the continuous spin haptic. Not used by any value/rotation math.
        float m_LastKnobRotation = 0.0f;

        /// <summary>
        /// The object that is visually grabbed and manipulated
        /// </summary>
        public Transform handle
        {
            get => m_Handle;
            set => m_Handle = value;
        }

        /// <summary>
        /// The value of the knob
        /// </summary>
        public float value
        {
            get => m_Value;
            set
            {
                SetValue(value);
                if (!m_DecoupleVisualFromValue)
                    SetKnobRotation(ValueToRotation());
            }
        }

        /// <summary>
        /// Whether this knob's rotation should be clamped by the angle limits
        /// </summary>
        public bool clampedMotion
        {
            get => m_ClampedMotion;
            set => m_ClampedMotion = value;
        }

        /// <summary>
        /// When enabled, setting <see cref="value"/> directly in code will not snap the handle's
        /// visual rotation to match. While actively grabbed, the handle spins continuously at a
        /// rate driven by how far it's twisted from the grab start, while <see cref="value"/>
        /// still reflects that twist offset directly. Releasing stops the spin immediately,
        /// unless <see cref="useMomentum"/> is also enabled.
        /// </summary>
        public bool decoupleVisualFromValue
        {
            get => m_DecoupleVisualFromValue;
            set => m_DecoupleVisualFromValue = value;
        }

        /// <summary>
        /// Only used when <see cref="decoupleVisualFromValue"/> is enabled. When true (default),
        /// holding the controller twisted at a constant offset keeps the handle spinning at a
        /// constant rate for as long as it's held. When false, the spin instead only reflects
        /// the controller's actual frame-to-frame movement, so it stops as soon as the
        /// controller stops moving, even while still held twisted away from the grab start.
        /// </summary>
        public bool holdToMaintainSpin
        {
            get => m_HoldToMaintainSpin;
            set => m_HoldToMaintainSpin = value;
        }

        /// <summary>
        /// Rotation of the knob at value '1'
        /// </summary>
        public float maxAngle
        {
            get => m_MaxAngle;
            set => m_MaxAngle = value;
        }

        /// <summary>
        /// Rotation of the knob at value '0'
        /// </summary>
        public float minAngle
        {
            get => m_MinAngle;
            set => m_MinAngle = value;
        }

        /// <summary>
        /// When enabled (and <see cref="decoupleVisualFromValue"/> is on), releasing the wheel
        /// while it's spinning makes it coast at its last spin rate and decelerate via friction
        /// to a stop, instead of halting immediately.
        /// </summary>
        public bool useMomentum
        {
            get => m_UseMomentum;
            set => m_UseMomentum = value;
        }

        /// <summary>
        /// How quickly momentum decays after release, in degrees/sec^2. Only used when
        /// <see cref="useMomentum"/> is enabled.
        /// </summary>
        public float friction
        {
            get => m_Friction;
            set => m_Friction = value;
        }

        /// <summary>
        /// The position of the interactor controls rotation when outside this radius
        /// </summary>
        public float positionTrackedRadius
        {
            get => m_PositionTrackedRadius;
            set => m_PositionTrackedRadius = value;
        }

        /// <summary>
        /// When enabled, if the interactor moves too far from the handle while dragging, the
        /// grab is force-released, instead of continuing to track rotation from far away.
        /// </summary>
        public bool releaseOnDragOutOfRange
        {
            get => m_ReleaseOnDragOutOfRange;
            set => m_ReleaseOnDragOutOfRange = value;
        }

        /// <summary>
        /// Distance from the handle beyond which an active drag is force-released. Only used
        /// when <see cref="releaseOnDragOutOfRange"/> is enabled.
        /// </summary>
        public float dragReleaseDistance
        {
            get => m_DragReleaseDistance;
            set => m_DragReleaseDistance = value;
        }

        /// <summary>
        /// Whether this knob sends haptic pulses at all (touch, grab, and continuous spin)
        /// </summary>
        public bool hapticsEnabled
        {
            get => m_HapticsEnabled;
            set => m_HapticsEnabled = value;
        }

        /// <summary>
        /// Events to trigger when the knob is rotated
        /// </summary>
        public ValueChangeEvent onValueChange => m_OnValueChange;

        void Start()
        {
            m_RestValue = m_Value;
            SetValue(m_Value);
            SetKnobRotation(ValueToRotation());
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            selectEntered.AddListener(StartGrab);
            selectExited.AddListener(EndGrab);
            hoverEntered.AddListener(OnHoverEntered);
            hoverExited.AddListener(OnHoverExited);
        }

        protected override void OnDisable()
        {
            selectEntered.RemoveListener(StartGrab);
            selectExited.RemoveListener(EndGrab);
            hoverEntered.RemoveListener(OnHoverEntered);
            hoverExited.RemoveListener(OnHoverExited);
            m_MomentumOffset = 0.0f;
            m_IsCoasting = false;
            base.OnDisable();
        }

        // Fires once the moment a controller starts hovering/touching the knob -
        // a light "you're near it" tap, distinct from the firmer grab pulse.
        void OnHoverEntered(HoverEnterEventArgs args)
        {
            if (m_HapticsEnabled)
                SendHaptic(args.interactorObject, m_TouchHapticAmplitude, m_TouchHapticDuration);
        }

        // Fires the moment a controller stops hovering/touching the knob (hand pulled away) -
        // a light "you've left it" tap, mirroring OnHoverEntered.
        void OnHoverExited(HoverExitEventArgs args)
        {
            if (m_HapticsEnabled)
                SendHaptic(args.interactorObject, m_UntouchHapticAmplitude, m_UntouchHapticDuration);
        }

        void StartGrab(SelectEnterEventArgs args)
        {
            m_Interactor = args.interactorObject;

            m_PositionAngles.Reset();
            m_UpVectorAngles.Reset();
            m_ForwardVectorAngles.Reset();

            UpdateBaseKnobRotation();

            // Start the spin integrator from wherever the mesh currently sits, so a decoupled
            // grab never causes a visual snap at the start of the grab.
            m_VisualSpinAngle = m_Handle != null ? m_Handle.localEulerAngles.y : 0.0f;

            // Baseline for the continuous spin haptic's speed calculation, so the very first
            // frame of a grab doesn't register a huge (and wrong) angular speed.
            m_LastKnobRotation = m_BaseKnobRotation;

            // One-shot pulse for the moment of grabbing, distinct from the touch pulse and
            // from the continuous while-turning pulse below.
            if (m_HapticsEnabled)
                SendHaptic(m_Interactor, m_GrabHapticAmplitude, m_GrabHapticDuration);

            UpdateRotation(true);
        }

        void EndGrab(SelectExitEventArgs args)
        {
            m_Interactor = null;

            // Hand off the last live spin rate to momentum coasting, if enabled. Otherwise
            // stop immediately, same as before this feature existed.
            if (m_DecoupleVisualFromValue && m_UseMomentum)
            {
                m_IsCoasting = Mathf.Abs(m_MomentumOffset) > 0.01f;
            }
            else
            {
                m_MomentumOffset = 0.0f;
                m_IsCoasting = false;
            }
        }

        public override void ProcessInteractable(XRInteractionUpdateOrder.UpdatePhase updatePhase)
        {
            base.ProcessInteractable(updatePhase);

            if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic)
            {
                if (isSelected)
                {
                    UpdateRotation();
                }
                else if (m_IsCoasting)
                {
                    UpdateMomentum();
                }
            }
        }

        void UpdateRotation(bool freshCheck = false)
        {
            // Are we in position offset or direction rotation mode?
            var interactorTransform = m_Interactor.GetAttachTransform(this);

            // We cache the three potential sources of rotation - the position offset, the forward vector of the controller, and up vector of the controller
            // We store any data used for determining which rotation to use, then flatten the vectors to the local xz plane
            var localOffset = transform.InverseTransformVector(interactorTransform.position - m_Handle.position);
            localOffset.y = 0.0f;
            var radiusOffset = transform.TransformVector(localOffset).magnitude;
            localOffset.Normalize();

            // Check this before the position-driven dead zone below inflates radiusOffset, so
            // the release distance means what it says: actual distance from the handle.
            if (m_ReleaseOnDragOutOfRange && radiusOffset > m_DragReleaseDistance)
            {
                interactionManager.SelectExit(m_Interactor, this);
                return;
            }

            var localForward = transform.InverseTransformDirection(interactorTransform.forward);
            var localY = Math.Abs(localForward.y);
            localForward.y = 0.0f;
            localForward.Normalize();

            var localUp = transform.InverseTransformDirection(interactorTransform.up);
            localUp.y = 0.0f;
            localUp.Normalize();


            if (m_PositionDriven && !freshCheck)
                radiusOffset *= (1.0f + k_ModeSwitchDeadZone);

            // Determine when a certain source of rotation won't contribute - in that case we bake in the offset it has applied
            // and set a new anchor when they can contribute again
            if (radiusOffset >= m_PositionTrackedRadius)
            {
                if (!m_PositionDriven || freshCheck)
                {
                    m_PositionAngles.SetBaseFromVector(localOffset);
                    m_PositionDriven = true;
                }
            }
            else
                m_PositionDriven = false;

            // If it's not a fresh check, then we weight the local Y up or down to keep it from flickering back and forth at boundaries
            if (!freshCheck)
            {
                if (!m_UpVectorDriven)
                    localY *= (1.0f - (k_ModeSwitchDeadZone * 0.5f));
                else
                    localY *= (1.0f + (k_ModeSwitchDeadZone * 0.5f));
            }

            if (localY > 0.707f)
            {
                if (!m_UpVectorDriven || freshCheck)
                {
                    m_UpVectorAngles.SetBaseFromVector(localUp);
                    m_UpVectorDriven = true;
                }
            }
            else
            {
                if (m_UpVectorDriven || freshCheck)
                {
                    m_ForwardVectorAngles.SetBaseFromVector(localForward);
                    m_UpVectorDriven = false;
                }
            }

            // Get angle from position
            if (m_PositionDriven)
                m_PositionAngles.SetTargetFromVector(localOffset);

            if (m_UpVectorDriven)
                m_UpVectorAngles.SetTargetFromVector(localUp);
            else
                m_ForwardVectorAngles.SetTargetFromVector(localForward);

            // Raw, unclamped knob rotation driven purely by interactor motion
            var rawKnobRotation = m_BaseKnobRotation - ((m_UpVectorAngles.totalOffset + m_ForwardVectorAngles.totalOffset) * m_TwistSensitivity) - m_PositionAngles.totalOffset;

            // How far the controller is currently twisted away from where the grab started.
            // This drives both the emitted value and, when decoupled, the mesh's spin rate.
            var offsetFromBase = rawKnobRotation - m_BaseKnobRotation;

            // Keep this in sync every frame so EndGrab always has the last live rate on hand,
            // in case momentum coasting picks up from here.
            m_MomentumOffset = offsetFromBase;

            // Continuous pulse while actively spinning, scaled to how fast it's being turned
            // right now (actual angular speed, not offsetFromBase which is cumulative twist
            // from grab start rather than a rate). Also the real per-frame movement, reused
            // below to drive decoupled spin when Hold To Maintain Spin is off.
            var rotationDeltaThisFrame = rawKnobRotation - m_LastKnobRotation;
            if (m_HapticsEnabled)
            {
                var angularSpeed = Mathf.Abs(rotationDeltaThisFrame) / Mathf.Max(Time.deltaTime, 0.0001f);
                var amplitude = Mathf.Clamp01(angularSpeed / m_SpinHapticFullSpeedThreshold) * m_SpinHapticAmplitude;
                SendHaptic(m_Interactor, amplitude, Time.deltaTime);
            }
            m_LastKnobRotation = rawKnobRotation;

            // Clamp to range - this clamped version drives the value calculation below
            var knobRotation = rawKnobRotation;
            if (m_ClampedMotion)
                knobRotation = Mathf.Clamp(knobRotation, m_MinAngle, m_MaxAngle);

            if (m_DecoupleVisualFromValue)
            {
                // Spin continuously: the further the twist offset, the faster the mesh keeps
                // turning, every frame, for as long as the offset is held. No offset means no
                // spin. This never snaps to a position, so it never "freezes" at a value.
                //
                // Hold To Maintain Spin off: instead of tracking the held offset, only advance
                // by this frame's real movement (the same delta used for the spin haptic above).
                // A constant offset held still produces zero delta, so the spin stops the moment
                // the controller itself stops moving, even if it's still twisted away from base.
                m_VisualSpinAngle += m_HoldToMaintainSpin
                    ? offsetFromBase * Time.deltaTime
                    : rotationDeltaThisFrame;
                SetKnobRotation(m_VisualSpinAngle);

                // Anchor to the fixed rest value, not m_BaseKnobRotation - m_BaseKnobRotation
                // is re-derived from m_Value at the start of every grab, so it can drift if a
                // new grab starts before a previous momentum coast has fully settled. m_RestValue
                // never changes, so releasing always settles back to the same value.
                var speedValue = m_RestValue + offsetFromBase / (m_MaxAngle - m_MinAngle);
                if (m_ClampedMotion)
                    speedValue = Mathf.Clamp01(speedValue);
                SetValue(speedValue);
            }
            else
            {
                // Coupled: mesh tracks the (possibly clamped) position directly, as before.
                SetKnobRotation(knobRotation);

                // Reverse the clamped rotation to get value, so value tracking respects the limits
                var positionValue = (knobRotation - m_MinAngle) / (m_MaxAngle - m_MinAngle);
                SetValue(positionValue);
            }
        }

        void UpdateMomentum()
        {
            // Decelerate the offset toward zero at a constant rate (friction), then keep
            // spinning and reporting value from whatever offset remains - same math as the
            // live grabbed path, just driven by a decaying virtual offset instead of a real one.
            var decay = m_Friction * Time.deltaTime;
            if (Mathf.Abs(m_MomentumOffset) <= decay)
            {
                m_MomentumOffset = 0.0f;
                m_IsCoasting = false;
            }
            else
            {
                m_MomentumOffset -= decay * Mathf.Sign(m_MomentumOffset);
            }

            m_VisualSpinAngle += m_MomentumOffset * Time.deltaTime;
            SetKnobRotation(m_VisualSpinAngle);

            // Same fixed-rest-value anchor as the live grabbed path - always settles back to
            // m_RestValue exactly as m_MomentumOffset decays to 0, never a drifted baseline.
            var speedValue = m_RestValue + m_MomentumOffset / (m_MaxAngle - m_MinAngle);
            if (m_ClampedMotion)
                speedValue = Mathf.Clamp01(speedValue);
            SetValue(speedValue);
        }

        void SetKnobRotation(float angle)
        {
            if (m_AngleIncrement > 0)
            {
                var normalizeAngle = angle - m_MinAngle;
                angle = (Mathf.Round(normalizeAngle / m_AngleIncrement) * m_AngleIncrement) + m_MinAngle;
            }

            if (m_Handle != null)
                m_Handle.localEulerAngles = new Vector3(0.0f, angle, 0.0f);
        }

        void SetValue(float value)
        {
            if (m_ClampedMotion)
                value = Mathf.Clamp01(value);

            if (m_AngleIncrement > 0)
            {
                var angleRange = m_MaxAngle - m_MinAngle;
                var angle = Mathf.Lerp(0.0f, angleRange, value);
                angle = Mathf.Round(angle / m_AngleIncrement) * m_AngleIncrement;
                value = Mathf.InverseLerp(0.0f, angleRange, angle);
            }

            m_Value = value;
            m_OnValueChange.Invoke(m_Value);
        }

        float ValueToRotation()
        {
            return m_ClampedMotion ? Mathf.Lerp(m_MinAngle, m_MaxAngle, m_Value) : Mathf.LerpUnclamped(m_MinAngle, m_MaxAngle, m_Value);
        }

        void UpdateBaseKnobRotation()
        {
            m_BaseKnobRotation = Mathf.LerpUnclamped(m_MinAngle, m_MaxAngle, m_Value);
        }

        // Sends a short haptic pulse to whichever controller is behind the given interactor.
        // As of XRI 3.x, IXRHapticImpulseProvider only exposes a channel *group* (GetChannelGroup) -
        // it has no SendHapticImpulse method of its own, and the old XRBaseController path is
        // deprecated. The supported way to fire a one-off impulse is via a HapticImpulsePlayer
        // component on the controller GameObject (added automatically by the XRI default rig,
        // or add one manually if you're on a custom rig). If nothing fires, the most likely
        // culprit is a missing HapticImpulsePlayer component on your controller - check that
        // first before assuming the code is wrong.
        static void SendHaptic(IXRInteractor interactor, float amplitude, float duration)
        {
            if (interactor == null || amplitude <= 0.0f)
                return;

            var hapticImpulsePlayer = interactor.transform.GetComponentInParent<HapticImpulsePlayer>();
            if (hapticImpulsePlayer != null)
                hapticImpulsePlayer.SendHapticImpulse(amplitude, duration);
        }

        static float ShortestAngleDistance(float start, float end, float max)
        {
            var angleDelta = end - start;
            var angleSign = Mathf.Sign(angleDelta);

            angleDelta = Math.Abs(angleDelta) % max;
            if (angleDelta > (max * 0.5f))
                angleDelta = -(max - angleDelta);

            return angleDelta * angleSign;
        }

        void OnDrawGizmosSelected()
        {
            const int k_CircleSegments = 16;
            const float k_SegmentRatio = 1.0f / k_CircleSegments;

            // Nothing to do if position radius is too small
            if (m_PositionTrackedRadius <= Mathf.Epsilon)
                return;

            // Draw a circle from the handle point at size of position tracked radius
            var circleCenter = transform.position;

            if (m_Handle != null)
                circleCenter = m_Handle.position;

            var circleX = transform.right;
            var circleY = transform.forward;

            Gizmos.color = Color.green;
            var segmentCounter = 0;
            while (segmentCounter < k_CircleSegments)
            {
                var startAngle = (float)segmentCounter * k_SegmentRatio * 2.0f * Mathf.PI;
                segmentCounter++;
                var endAngle = (float)segmentCounter * k_SegmentRatio * 2.0f * Mathf.PI;

                Gizmos.DrawLine(circleCenter + (Mathf.Cos(startAngle) * circleX + Mathf.Sin(startAngle) * circleY) * m_PositionTrackedRadius,
                    circleCenter + (Mathf.Cos(endAngle) * circleX + Mathf.Sin(endAngle) * circleY) * m_PositionTrackedRadius);
            }
        }

        void OnValidate()
        {
            if (m_ClampedMotion)
                m_Value = Mathf.Clamp01(m_Value);

            if (m_MinAngle > m_MaxAngle)
                m_MinAngle = m_MaxAngle;

            SetKnobRotation(ValueToRotation());
        }
    }
}