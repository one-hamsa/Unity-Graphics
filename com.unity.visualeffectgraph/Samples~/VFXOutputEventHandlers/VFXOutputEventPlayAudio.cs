#if VFX_OUTPUTEVENT_AUDIO
using UnityEngine.Events;

namespace UnityEngine.VFX.Utility
{
    [ExecuteAlways]
    [RequireComponent(typeof(VisualEffect))]
    public class VFXOutputEventPlayAudio : VFXOutputEventHandler
    {
        public override bool canExecuteInEditor => true;

        public AudioSource audioSource;

        public override void OnVFXOutputEvent(VFXEventAttribute eventAttribute)
        {
            if(audioSource != null)
            {
                audioSource.Play();
            }
        }
    }
}
#endif