// il2cpplab video-tracking builds only (IL2CPPLAB_VIDEO): everything here compiles away in
// every other configuration, editor included.
#if IL2CPPLAB_CAPTURE && IL2CPPLAB_VIDEO && !UNITY_EDITOR && (UNITY_ANDROID || UNITY_STANDALONE_WIN)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.XR;

namespace UnityEngine.Rendering.Universal
{
    /// <summary>
    /// videolab: records a 1/4-per-axis downsample of what the player sees, every frame
    /// of an active capture session, into &lt;session&gt;/video.mp4 (MediaCodec on Quest,
    /// Media Foundation on Windows — both in the recorder). Called from
    /// ScriptableRenderer.Execute after the stack's last camera rendered; issues one
    /// VIDEO_CAPTURE plugin event carrying the capture source and the rendered viewport,
    /// handled by the il2cpplab_gpu_probe plugin (Vulkan blit, or a sampling pass for
    /// FFR swapchains / D3D11 mip-chain + readback ring). Only display-presenting
    /// cameras are captured: the XR swapchain
    /// texture (left eye) in VR, the backbuffer in No-VR (VRPlugin_Manual test mode);
    /// RT-targeted cameras never claim a frame. Self-initializes on the first gated call and issues
    /// zero events while no capture is active (one P/Invoke per frame decides that).
    /// There is no control.txt switch - the build flavor is the opt-in.
    /// </summary>
    internal static class VideoLabUrpHook
    {
        [DllImport("__Internal")] static extern uint il2cpplab_video_control();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_video_sink();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_video_meta_sink();
        [DllImport("__Internal")] static extern void il2cpplab_video_visible_rect(uint x, uint y, uint w, uint h);
        [DllImport("__Internal")] static extern void il2cpplab_gpu_announce(uint flags);
        [DllImport("__Internal")] static extern uint perflab_marker_register(string name);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_meta_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_path_stats(
            out ulong reconstructed, out ulong blit, out ulong fallbacks,
            out uint lastReason, out uint lastUsage);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_enabled(int enabled);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_site(uint site);
        [DllImport("il2cpplab_gpu_probe")] static extern IntPtr il2cpplab_gpu_probe_event_func();

        // must match gpulab_probe.cpp: kOpVideoCapture / VideoCaptureRequest layout
        const int EventVideoCapture = 4;
        const int RequestBytes = 40;
        // No-VR backbuffer request: Vulkan resolves handle as a UnityRenderBuffer, D3D11
        // ignores the handle and fetches the swapchain backbuffer on the render thread
        // (a main-thread native pointer for the backbuffer is not an ID3D11Resource and
        // dereferencing it crashes the render thread)
        const int FlagBackbuffer = 1;
        // capture this FFR frame through the probe's sample-reconstruction pass instead
        // of the raw blit. Driven per frame by the recorder (video_control bit 1 =
        // FFR seen AND profilerControl.txt "video_reconstruct 1"); the blit is the
        // default - current Meta runtimes re-pack the swapchain on internal state
        // changes the sampler does not track, so reconstruction degrades mid-session.
        const int FlagSubsampled = 2;
        // requests are read on the render thread up to a few frames later; 8 slots is
        // several frames of headroom at one request per frame
        const int RequestRing = 8;

        static bool initTried;
        static IntPtr eventFunc;
        static IntPtr requests; // unmanaged request ring, process lifetime
        static int requestNext;
        static int lastFrame = -1;
        static int pluginEnabled = -1; // last value pushed to the plugin; -1 = never
        static bool reconstruct; // video_control bit 1 this frame (see FlagSubsampled)
        static bool visRectReported; // the occlusion-mesh visible rect went to the recorder
        static ulong lastBlitFrames;  // path-stats poll: blit count at the previous poll
        static uint lastLoggedReason; // last fallback reason already logged (log per change)
        static int pathPollCountdown = PathPollFrames;
        const int PathPollFrames = 900; // ~10s at 90Hz; the poll is one P/Invoke
        static XRDisplaySubsystem display;
        static readonly List<XRDisplaySubsystem> displays = new List<XRDisplaySubsystem>(1);
        // each GetNativeTexturePtr may sync the render thread, so pointers are cached per
        // texture object (the swapchain cycles through 2-3 RenderTexture instances)
        static readonly Dictionary<RenderTexture, IntPtr> nativePtrByTexture =
            new Dictionary<RenderTexture, IntPtr>(4);

        /// <summary>One VIDEO_CAPTURE event per frame, after the stack's final camera rendered.</summary>
        public static void CaptureFrame(CommandBuffer cmd, ref CameraData cameraData)
        {
            int frame = Time.frameCount;
            if (frame == lastFrame)
                return;
            // only the camera that presents to the display is "what the player sees".
            // RT-targeted cameras (photo booth, previews, reflections) and non-final
            // stack cameras must not claim the frame: capturing one alternates the
            // encoder geometry with the screen's (the recorder keeps one geometry per
            // session and drops the rest) and RT content reads y-flipped on D3D11.
            if (!cameraData.resolveToScreen)
                return;
            uint control = il2cpplab_video_control();
            bool on = (control & 1) != 0;
            reconstruct = (control & 2) != 0;
            if (on && !initTried)
                Init();
            if (eventFunc == IntPtr.Zero)
                return;
            int enabled = on ? 1 : 0;
            if (enabled != pluginEnabled)
            {
                pluginEnabled = enabled;
                il2cpplab_video_probe_set_enabled(enabled);
            }
            if (!on)
                return;

            long handle;
            int texW, texH, vpX, vpY, vpW, vpH, flags;
            if (cameraData.xr.enabled)
            {
                RenderTexture rt = SwapchainTexture();
                if (rt == null)
                    return; // no acquired swapchain image this frame (headset idle)
                IntPtr tex = CachedNativePtr(rt);
                if (tex == IntPtr.Zero)
                    return;
                handle = (long)tex;
                texW = rt.width;
                texH = rt.height;
                // dynamic resolution renders into a viewport subrect of the fixed-size
                // swapchain texture; the probe blits exactly that rect
                Rect vp = cameraData.xr.GetViewport();
                vpX = (int)vp.x;
                vpY = (int)vp.y;
                vpW = (int)vp.width;
                vpH = (int)vp.height;
                flags = reconstruct ? FlagSubsampled : 0;
                if (!visRectReported)
                    ReportVisibleRect(ref cameraData);
                // reconstruction frames falling back to the blit mean artifacted
                // video - surface the probe's reason in the session log
                if (reconstruct && --pathPollCountdown <= 0)
                {
                    pathPollCountdown = PathPollFrames;
                    il2cpplab_video_probe_path_stats(out ulong rec, out ulong blit,
                        out ulong falls, out uint reason, out uint usage);
                    if (blit > lastBlitFrames && reason != lastLoggedReason)
                    {
                        lastLoggedReason = reason;
                        Debug.LogWarning($"[il2cpplab] videolab FFR reconstruction degraded: " +
                            $"{rec} reconstructed / {blit} blit frames, {falls} fallbacks, " +
                            $"last reason {reason} (1 observe, 2 usage, 3 static-init, " +
                            $"4 sized-init, 5 descriptor), usage 0x{usage:x}");
                    }
                    lastBlitFrames = blit;
                }
            }
            else
            {
                // No-VR (VRPlugin_Manual): the camera renders to the backbuffer; the
                // probe resolves it on the render thread (see FlagBackbuffer). The
                // RenderBuffer handle is only meaningful to the Vulkan backend.
                handle = (long)Display.main.colorBuffer.GetNativeRenderBufferPtr();
                texW = Display.main.renderingWidth;
                texH = Display.main.renderingHeight;
                vpX = 0; vpY = 0; vpW = texW; vpH = texH;
                flags = FlagBackbuffer;
            }
            lastFrame = frame;

            IntPtr slot = new IntPtr(requests.ToInt64() + requestNext * RequestBytes);
            requestNext = (requestNext + 1) % RequestRing;
            Marshal.WriteInt64(slot, 0, handle);
            Marshal.WriteInt32(slot, 8, frame);
            Marshal.WriteInt32(slot, 12, texW);
            Marshal.WriteInt32(slot, 16, texH);
            Marshal.WriteInt32(slot, 20, vpX);
            Marshal.WriteInt32(slot, 24, vpY);
            Marshal.WriteInt32(slot, 28, vpW);
            Marshal.WriteInt32(slot, 32, vpH);
            Marshal.WriteInt32(slot, 36, flags);
            cmd.IssuePluginEventAndData(eventFunc, EventVideoCapture, slot);
        }

        // The occlusion mesh masks the pixels the lens never shows (for the left eye,
        // mostly a full-height nasal band on the right); they are never rendered, so the
        // captured video carries garbage there. Rasterize the mesh once into a coarse
        // grid, take the bounding rect of the UNMASKED cells, and hand it to the
        // recorder — it rides the session's VIDEO_INFO record and viewers crop to it.
        // Mesh vertices are in [0,1] viewport uv, y down like the video rows (the
        // XROcclusionMesh shader maps uv (0,0) to clip (-1,+1)).
        static void ReportVisibleRect(ref CameraData cameraData)
        {
            visRectReported = true;
            Mesh mesh = cameraData.xr.GetOcclusionMesh();
            if (mesh == null)
            {
                Debug.Log("[il2cpplab] videolab: no occlusion mesh on this XR setup - " +
                          "visible rect not recorded, viewers show the full frame");
                return;
            }
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;
            const int Grid = 128;
            bool[] masked = new bool[Grid * Grid];
            for (int t = 0; t + 2 < tris.Length; t += 3) {
                Vector2 a = verts[tris[t]], b = verts[tris[t + 1]], c = verts[tris[t + 2]];
                int x0 = Mathf.Clamp((int)(Mathf.Min(a.x, b.x, c.x) * Grid), 0, Grid - 1);
                int x1 = Mathf.Clamp((int)(Mathf.Max(a.x, b.x, c.x) * Grid), 0, Grid - 1);
                int y0 = Mathf.Clamp((int)(Mathf.Min(a.y, b.y, c.y) * Grid), 0, Grid - 1);
                int y1 = Mathf.Clamp((int)(Mathf.Max(a.y, b.y, c.y) * Grid), 0, Grid - 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) {
                        // conservative: a cell is masked if its center is in the triangle
                        var p = new Vector2((x + 0.5f) / Grid, (y + 0.5f) / Grid);
                        float d1 = Cross(p, a, b), d2 = Cross(p, b, c), d3 = Cross(p, c, a);
                        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
                        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
                        if (!(neg && pos))
                            masked[y * Grid + x] = true;
                    }
            }
            int minX = Grid, minY = Grid, maxX = -1, maxY = -1;
            for (int y = 0; y < Grid; y++)
                for (int x = 0; x < Grid; x++)
                    if (!masked[y * Grid + x]) {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0 || (minX == 0 && minY == 0 && maxX == Grid - 1 && maxY == Grid - 1))
                return; // fully masked (broken mesh) or nothing masked - no crop to report
            uint vx = (uint)(minX * 10000 / Grid);
            uint vy = (uint)(minY * 10000 / Grid);
            uint vw = (uint)((maxX + 1) * 10000 / Grid) - vx;
            uint vh = (uint)((maxY + 1) * 10000 / Grid) - vy;
            il2cpplab_video_visible_rect(vx, vy, vw, vh);
            Debug.Log($"[il2cpplab] videolab visible rect {vx / 100f}%..{(vx + vw) / 100f}% x " +
                      $"{vy / 100f}%..{(vy + vh) / 100f}% (occlusion mesh, {tris.Length / 3} tris)");
        }

        static float Cross(Vector2 p, Vector2 a, Vector2 b)
        {
            return (p.x - a.x) * (b.y - a.y) - (p.y - a.y) * (b.x - a.x);
        }

        static IntPtr CachedNativePtr(RenderTexture rt)
        {
            if (!nativePtrByTexture.TryGetValue(rt, out IntPtr tex))
            {
                tex = rt.GetNativeTexturePtr();
                nativePtrByTexture.Add(rt, tex);
                if (nativePtrByTexture.Count > 16)
                    nativePtrByTexture.Clear(); // target textures were re-created; re-resolve
            }
            return tex;
        }

        static RenderTexture SwapchainTexture()
        {
            if (display == null || !display.running)
            {
                SubsystemManager.GetSubsystems(displays);
                display = null;
                for (int i = 0; i < displays.Count; i++)
                    if (displays[i].running)
                    {
                        display = displays[i];
                        break;
                    }
                if (display == null)
                    return null;
            }
            if (display.GetRenderPassCount() < 1)
                return null;
            // multiview single-pass: one render pass, a 2-layer texture array; the probe
            // captures array layer 0 (the left eye)
            return display.GetRenderTextureForRenderPass(0);
        }

        static void Init()
        {
            initTried = true;
            try
            {
                eventFunc = il2cpplab_gpu_probe_event_func();
                IntPtr sink = il2cpplab_video_sink();
                if (sink == IntPtr.Zero)
                {
                    // recorder built without videolab (video flavor off / unsupported platform)
                    eventFunc = IntPtr.Zero;
                    Debug.LogError("[il2cpplab] recorder has no video sink — videolab unavailable");
                    return;
                }
                il2cpplab_video_probe_set_sink(sink);
                // per-frame capture-route diagnostics (VIDEO_PATH records in the capture)
                il2cpplab_video_probe_set_meta_sink(il2cpplab_video_meta_sink());
                // the capture's own GPU work shows as "videolab.capture" in the per-pass table
                il2cpplab_video_probe_set_site(perflab_marker_register("videolab.capture"));
                requests = Marshal.AllocHGlobal(RequestBytes * RequestRing);
                il2cpplab_gpu_announce(0x8); // session_header.gpu_flags bit 3: videolab
                Debug.Log("[il2cpplab] videolab frame capture connected");
            }
            catch (DllNotFoundException e)
            {
                eventFunc = IntPtr.Zero;
                Debug.LogError("[il2cpplab] il2cpplab_gpu_probe plugin missing — videolab unavailable");
                Debug.LogException(e);
            }
        }
    }
}
#endif
