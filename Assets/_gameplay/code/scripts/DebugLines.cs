using UnityEngine;

namespace Delirium.Wheelchair
{
    /// <summary>
    /// Thin in-headset debug lines. These are real objects in the scene, not gizmos, so they render
    /// in the game view and in both eyes - provided the material is stereo-aware, which is why this
    /// reaches for a URP shader first. Sprites/Default and other built-in shaders don't support
    /// single-pass instanced stereo and end up drawn in one eye, or offset.
    /// </summary>
    public static class DebugLines
    {
        static readonly int BaseColourId = Shader.PropertyToID("_BaseColor");
        static readonly int ColourId = Shader.PropertyToID("_Color");

        public static LineRenderer Create(string name, float width = 0.004f)
        {
            var host = new GameObject(name);
            LineRenderer line = host.AddComponent<LineRenderer>();
            line.positionCount = 2;
            line.useWorldSpace = true;
            line.startWidth = width;
            line.endWidth = width;
            line.alignment = LineAlignment.View;
            line.textureMode = LineTextureMode.Stretch;
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            line.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            if (shader != null)
            {
                var material = new Material(shader) { enableInstancing = true };
                line.material = material;
            }

            return line;
        }

        public static void Set(LineRenderer line, Vector3 from, Vector3 to, Color colour)
        {
            if (line == null) return;

            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.startColor = colour;
            line.endColor = colour;

            Material material = line.material;
            if (material == null) return;

            if (material.HasProperty(BaseColourId)) material.SetColor(BaseColourId, colour);
            if (material.HasProperty(ColourId)) material.SetColor(ColourId, colour);
        }
    }
}
