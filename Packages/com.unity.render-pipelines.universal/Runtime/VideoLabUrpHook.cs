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
    /// handled by the il2cpplab_gpu_probe plugin (Vulkan blit / D3D11 mip-chain +
    /// readback ring). The source is resolved per mode: the XR swapchain texture
    /// (left eye) in VR, the camera's target texture or the backbuffer in No-VR
    /// (VRPlugin_Manual test mode). Self-initializes on the first gated call and issues
    /// zero events while no capture is active (one P/Invoke per frame decides that).
    /// There is no control.txt switch - the build flavor is the opt-in.
    /// </summary>
    internal static class VideoLabUrpHook
    {
        [DllImport("__Internal")] static extern uint il2cpplab_video_control();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_video_sink();
        [DllImport("__Internal")] static extern void il2cpplab_gpu_announce(uint flags);
        [DllImport("__Internal")] static extern uint perflab_marker_register(string name);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_enabled(int enabled);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_video_probe_set_site(uint site);
        [DllImport("il2cpplab_gpu_probe")] static extern IntPtr il2cpplab_gpu_probe_event_func();

        // must match gpulab_probe.cpp: kOpVideoCapture / VideoCaptureRequest layout
        const int EventVideoCapture = 4;
        const int RequestBytes = 40;
        const int FlagRenderBuffer = 1; // handle is a UnityRenderBuffer (backbuffer path)
        // requests are read on the render thread up to a few frames later; 8 slots is
        // several frames of headroom at one request per frame
        const int RequestRing = 8;

        static bool initTried;
        static IntPtr eventFunc;
        static IntPtr requests; // unmanaged request ring, process lifetime
        static int requestNext;
        static int lastFrame = -1;
        static int pluginEnabled = -1; // last value pushed to the plugin; -1 = never
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
            bool on = il2cpplab_video_control() != 0;
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
                flags = 0;
            }
            else if (cameraData.targetTexture != null)
            {
                RenderTexture rt = cameraData.targetTexture;
                IntPtr tex = CachedNativePtr(rt);
                if (tex == IntPtr.Zero)
                    return;
                handle = (long)tex;
                texW = rt.width;
                texH = rt.height;
                vpX = 0; vpY = 0; vpW = texW; vpH = texH;
                flags = 0;
            }
            else
            {
                // No-VR (VRPlugin_Manual): the camera renders to the backbuffer; the
                // probe resolves the RenderBuffer to its native texture on the render
                // thread, so no pointer caching is needed here
                handle = (long)Display.main.colorBuffer.GetNativeRenderBufferPtr();
                if (handle == 0)
                    return;
                texW = Display.main.renderingWidth;
                texH = Display.main.renderingHeight;
                vpX = 0; vpY = 0; vpW = texW; vpH = texH;
                flags = FlagRenderBuffer;
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
