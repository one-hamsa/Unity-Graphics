namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// Splits the target camera's opaque forward pass into a near-field pass and a far-field pass.
    /// The far pass renders with cheaper pass-scoped shader state — no main light shadows,
    /// single-tap UDShadow, mono view direction (see <c>UDFarFieldDrawObjectsPass</c>).
    /// The game (NearFarSplitManager) tags far-field renderers by setting their renderingLayerMask
    /// to <see cref="FarFieldRenderingLayer"/>; everything else draws in the near pass.
    /// </summary>
    public static class UDNearFarSplit
    {
        /// <summary>Rendering layer bit that marks far-field renderers. High bit, clear of URP light/decal layers.</summary>
        public const uint FarFieldRenderingLayer = 1u << 30;

        /// <summary>Master switch, driven by the game. Off = stock single opaque pass everywhere.</summary>
        public static bool enabled;

        /// <summary>Only this camera's rendering is split; every other camera renders the stock single pass.</summary>
        public static Camera targetCamera;

        public static bool ShouldSplit(Camera camera)
        {
            return enabled && ReferenceEquals(camera, targetCamera);
        }
    }
}
