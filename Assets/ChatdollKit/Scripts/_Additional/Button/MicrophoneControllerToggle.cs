using UnityEngine;

namespace ChatdollKit.UI
{
    public class MicrophoneControllerToggle : MonoBehaviour
    {
        [SerializeField]
        private GameObject microphoneController;

        public void OnToggleButton()
        {
            if (microphoneController != null)
            {
                microphoneController.SetActive(!microphoneController.activeSelf);
            }
        }
    }
}