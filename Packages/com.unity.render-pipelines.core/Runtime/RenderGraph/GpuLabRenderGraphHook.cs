// il2cpplab gpu-tracking builds only (IL2CPPLAB_GPU): everything here compiles away in
// every other configuration, editor included.
#if IL2CPPLAB_CAPTURE && IL2CPPLAB_GPU && !UNITY_EDITOR && (UNITY_ANDROID || UNITY_STANDALONE_WIN)
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace UnityEngine.Rendering.RenderGraphModule
{
    /// <summary>
    /// gpulab tier B: brackets every executed render graph pass with GPU timestamp events
    /// handled by the il2cpplab_gpu_probe native plugin, which forwards per-pass GPU
    /// durations to the il2cpplab recorder (per-pass spans on the capture timeline).
    /// Self-initializes on the first call, gates itself once per frame on
    /// il2cpplab_gpu_control() (`gpu_spans` in profilerControl.txt), and issues zero
    /// events while no capture is active.
    /// Pass identity is the pass's sampler name, interned through the perflab marker
    /// table so the parser names spans for free. A site's first pass (and any later change)
    /// also reports its color target's shape - dims / format / MSAA / depth - as pass
    /// metadata, the bandwidth context behind the span; the D3D11 backend additionally
    /// gets a per-pass pipeline-statistics sink.
    /// </summary>
    /// <remarks>
    /// This lives in core rather than in URP because render graph pass execution is a core
    /// concern, and it has two paths: RenderGraph.ExecuteCompiledPass on the graph path and
    /// NativePassCompiler.ExecuteRenderGraphPass on the native-render-pass path. Both call
    /// BeginPass/EndPass. FrameSetup is called once per graph execution from
    /// RenderGraph.Execute before either path starts: the probe's frame setup resets query
    /// pools, which is illegal inside a render pass, and issuing it from the first pass
    /// instead would force the Vulkan backend to interrupt an open render pass - a tile
    /// resolve and reload on Quest.
    /// </remarks>
    internal static class GpuLabRenderGraphHook
    {
        [DllImport("__Internal")] static extern uint il2cpplab_gpu_control();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_gpu_span_sink();
        [DllImport("__Internal")] static extern IntPtr il2cpplab_gpu_stats_sink();
        [DllImport("__Internal")] static extern void il2cpplab_gpu_pass_meta(uint[] words, uint count);
        [DllImport("__Internal")] static extern uint perflab_marker_register(string name);
        [DllImport("__Internal")] static extern void il2cpplab_gpu_announce(uint flags);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_stats_sink(IntPtr sink);
        [DllImport("il2cpplab_gpu_probe")] static extern void il2cpplab_gpu_probe_set_enabled(int enabled);
        [DllImport("il2cpplab_gpu_probe")] static extern IntPtr il2cpplab_gpu_probe_event_func();

        // event ids - must match gpulab_probe.cpp
        const int EventFrameSetup = 1;
        const int EventPassBegin = 2;
        const int EventPassEnd = 3;

        static bool initTried;
        static IntPtr eventFunc;
        // reference-keyed: URP's samplers are stable instances (ProfilingSampler.Get for
        // built-in ids, one per pass object for custom passes)
        static readonly Dictionary<ProfilingSampler, uint> siteBySampler =
            new Dictionary<ProfilingSampler, uint>(64);
        // passes with no sampler still have a name; keyed separately so the common path
        // stays a reference compare
        static readonly Dictionary<string, uint> siteByName = new Dictionary<string, uint>(16);
        static int lastGateFrame = -1;
        static bool frameActive;
        static bool setupPending;
        static int pluginEnabled = -1; // last value pushed to the plugin; -1 = never

        /// <summary>
        /// Once per graph execution, outside any render pass: flushes the completed
        /// frame's query results and prepares this frame's slot.
        /// </summary>
        public static void FrameSetup(CommandBuffer cmd)
        {
            if (Time.frameCount != lastGateFrame)
                GateFrame(Time.frameCount);
            EmitSetupIfPending(cmd);
        }

        public static void BeginPass(CommandBuffer cmd, RenderGraphPass pass, RenderGraphResourceRegistry resources)
        {
            // render graph's immediateMode executes passes while recording, before
            // FrameSetup runs, so the gate cannot rely on having been primed
            if (Time.frameCount != lastGateFrame)
                GateFrame(Time.frameCount);
            if (!frameActive)
                return;
            EmitSetupIfPending(cmd);
            uint site = SiteOf(pass);
            EmitPassMeta(site, pass, resources);
            cmd.IssuePluginEventAndData(eventFunc, EventPassBegin, Pack(site, lastGateFrame));
        }

        public static void EndPass(CommandBuffer cmd, RenderGraphPass pass)
        {
            if (!frameActive)
                return;
            cmd.IssuePluginEventAndData(eventFunc, EventPassEnd, Pack(SiteOf(pass), lastGateFrame));
        }

        static void EmitSetupIfPending(CommandBuffer cmd)
        {
            if (!setupPending)
                return;
            setupPending = false;
            cmd.IssuePluginEventAndData(eventFunc, EventFrameSetup, Pack(0, lastGateFrame));
        }

        // A pass's color target shape (dims/format/MSAA/depth), once per site when first
        // seen or changed - the bandwidth context behind the pass's GPU time. Cleared when
        // a capture (re)starts so every session carries its own copy.
        static readonly uint[] metaWords = new uint[6];
        static readonly Dictionary<uint, ulong> metaBySite = new Dictionary<uint, ulong>(64);

        static void EmitPassMeta(uint site, RenderGraphPass pass, RenderGraphResourceRegistry resources)
        {
            if (site == 0 || resources == null || pass.colorBufferMaxIndex < 0)
                return;
            TextureHandle handle = pass.colorBufferAccess[0].textureHandle;
            if (!handle.IsValid())
                return;
            RTHandle target;
            try
            {
                target = resources.GetTexture(handle);
            }
            catch (InvalidOperationException)
            {
                return; // released or not yet created: nothing to describe
            }
            RenderTexture rt = target?.rt;
            if (rt == null)
                return; // imported / backbuffer target: no descriptor to read
            var d = rt.descriptor;
            ulong packed = ((ulong)(uint)d.width << 42) ^ ((ulong)(uint)d.height << 20)
                         ^ ((ulong)(uint)d.graphicsFormat << 6) ^ (uint)d.msaaSamples;
            if (metaBySite.TryGetValue(site, out ulong prev) && prev == packed)
                return;
            metaBySite[site] = packed;
            metaWords[0] = site;
            metaWords[1] = (uint)d.width;
            metaWords[2] = (uint)d.height;
            metaWords[3] = (uint)d.graphicsFormat;
            metaWords[4] = (uint)d.msaaSamples;
            metaWords[5] = (uint)d.depthBufferBits;
            il2cpplab_gpu_pass_meta(metaWords, 6);
        }

        static void GateFrame(int frame)
        {
            lastGateFrame = frame;
            bool spansOn = (il2cpplab_gpu_control() & 0x2) != 0;
            if (spansOn && !initTried)
                Init();
            if (eventFunc == IntPtr.Zero)
            {
                frameActive = false;
                setupPending = false;
                return;
            }
            int enabled = spansOn ? 1 : 0;
            if (enabled != pluginEnabled)
            {
                pluginEnabled = enabled;
                il2cpplab_gpu_probe_set_enabled(enabled);
                if (spansOn)
                    metaBySite.Clear(); // a capture (re)started: re-emit every pass's meta
            }
            frameActive = spansOn;
            setupPending = spansOn;
        }

        static void Init()
        {
            initTried = true;
            try
            {
                eventFunc = il2cpplab_gpu_probe_event_func();
                il2cpplab_gpu_probe_set_sink(il2cpplab_gpu_span_sink());
                // per-pass pipeline statistics (D3D11 backend; the probe no-ops elsewhere)
                il2cpplab_gpu_probe_set_stats_sink(il2cpplab_gpu_stats_sink());
                il2cpplab_gpu_announce(0x2); // session_header.gpu_flags bit 1: pass spans
                Debug.Log("[il2cpplab] gpu pass-span probe connected");
            }
            catch (DllNotFoundException e)
            {
                eventFunc = IntPtr.Zero;
                Debug.LogError("[il2cpplab] il2cpplab_gpu_probe plugin missing - per-pass GPU spans unavailable");
                Debug.LogException(e);
            }
        }

        static uint SiteOf(RenderGraphPass pass)
        {
            var sampler = pass.customSampler;
            if (sampler != null)
            {
                if (!siteBySampler.TryGetValue(sampler, out uint site))
                {
                    // 0 when the marker table is full: those spans stay unattributed
                    // rather than mis-attributed
                    site = perflab_marker_register(sampler.name);
                    siteBySampler.Add(sampler, site);
                }
                return site;
            }

            string name = pass.name;
            if (string.IsNullOrEmpty(name))
                return 0; // recorder treats site 0 as unattributed
            if (!siteByName.TryGetValue(name, out uint byName))
            {
                byName = perflab_marker_register(name);
                siteByName.Add(name, byName);
            }
            return byName;
        }

        // the probe decodes data as (site << 32 | frame_index)
        static IntPtr Pack(uint site, int frame)
        {
            return (IntPtr)(long)(((ulong)site << 32) | (uint)frame);
        }
    }
}
#endif
