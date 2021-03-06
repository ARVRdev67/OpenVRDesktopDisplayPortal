using UnityEngine;

[RequireComponent(typeof(Camera))]
public class AutoResizeCameraForRenderTexture : MonoBehaviour
{
    public Camera Camera
    {
        get { return _camera ?? (_camera = GetComponent<Camera>()); }
    }

    private Camera _camera;
    public void ResizeCamera(RenderTexture render, float orthoDiv)
    {
        Camera.orthographicSize = render.height / orthoDiv;
        Camera.aspect = (float)render.width / (float)render.height;
    }
}