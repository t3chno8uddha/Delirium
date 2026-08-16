using UnityEngine;
using UnityEngine.InputSystem;

public class HandAnimator : MonoBehaviour
{
    [SerializeField]InputActionProperty triggerValue, gripValue;
    [SerializeField] Animator handAnimator;
    void Update()
    {
        float trigger = triggerValue.action.ReadValue<float>();
        float grip = gripValue.action.ReadValue<float>();

        handAnimator.SetFloat("Trigger", trigger);
        handAnimator.SetFloat("Grip", grip);
    }
}
