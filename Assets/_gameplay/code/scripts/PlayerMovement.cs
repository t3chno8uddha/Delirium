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

    Vector2 wheel_velocity;

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
        // Smoothly move actual velocity toward target velocity
        wheel_velocity_l = Mathf.Lerp(wheel_velocity_l, wheel_velocity.x, inputSmooth * Time.deltaTime);
        wheel_velocity_r = Mathf.Lerp(wheel_velocity_r, wheel_velocity.y, inputSmooth * Time.deltaTime);

        // Return smoothed input vector
        return new Vector3(wheel_velocity_l, wheel_velocity_r, 0);
    }

    public void LeftWheelVelocity(float velocity)
    {
        wheel_velocity.y = (velocity - 0.5f) * -2f;
    }
    public void RightWheelVelocity(float velocity)
    {
        wheel_velocity.x = (velocity - 0.5f) * -2f;
    }
}