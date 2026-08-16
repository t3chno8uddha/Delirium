using Unity.VisualScripting;
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
    [SerializeField] float inputSmooth = 8f;

    void Update()
    {
        // Get smoothed wheelchair input
        wh_input = GetChairInput();

        // Move anchors sideways relative to the player
        anchor_R.transform.position += transform.right * wh_input.y * speed * Time.deltaTime;
        anchor_L.transform.position += transform.right * wh_input.x * speed * Time.deltaTime;

        // Cache anchor positions
        var aL = anchor_L.position;
        var aR = anchor_R.position;

        // Direction between anchors determines chair orientation
        Vector3 direction = aL - aR;

        // Rotate wheelchair to align with wheel axis
        transform.rotation = Quaternion.LookRotation(direction);

        // Position wheelchair between the two wheel anchors
        transform.position = Vector3.Lerp(aL, aR, 0.5f);
    }

    Vector3 GetChairInput()
    {
        // Target input values based on current frame input
        float target_l = 0;
        float target_r = 0;

        // Left wheel controlled by keyboard
        if (Input.GetKey(KeyCode.Q)) target_l += 1;
        if (Input.GetKey(KeyCode.A)) target_l -= 1;

        // Right wheel controlled by mouse scroll
        if (Input.GetKey(KeyCode.E)) target_r += 1;
        if (Input.GetKey(KeyCode.D)) target_r -= 1;

        // Smoothly move actual velocity toward target velocity
        wheel_velocity_l = Mathf.Lerp(wheel_velocity_l, target_l, inputSmooth * Time.deltaTime);
        wheel_velocity_r = Mathf.Lerp(wheel_velocity_r, target_r, inputSmooth * Time.deltaTime);

        // Return smoothed input vector
        return new Vector3(wheel_velocity_l, wheel_velocity_r, 0);
    }
}