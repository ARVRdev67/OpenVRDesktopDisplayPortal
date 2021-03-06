using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Valve.VR;
using Color = UnityEngine.Color;
using Image = UnityEngine.UI.Image;

public class HOTK_CompanionOverlay : HOTK_OverlayBase
{
    public HOTK_Overlay Overlay;
    public Texture OverlayTexture;
    public VROverlayInputMethod InputMethod = VROverlayInputMethod.None;

    public CompanionMode OverlayMode;
    private CompanionMode _overlayMode;

    public InterfaceAttachMode AttachMode;
    private InterfaceAttachMode _attachMode;

    public Action<HOTK_OverlayBase, bool> OnOverlayGazed;

    private bool _subscribed;

    public float RelativeAlpha = 1f;
    private float _relativeAlpha;
    public float RelativeScale = 1f;
    private float _relativeScale;

    private float _alpha;   // These are used to cache values and check for changes
    private float _scale;   // These are used to cache values and check for changes
    private uint _anchor;   // caches a HOTK_TrackedDevice ID for anchoring the Overlay, if applicable

    private Vector3 _companionEuler;

    public Vector3 CompanionOffset;
    private Vector3 _companionOffset;

    private Vector3 _pivotOffset;

    public GameObject Pivot
    {
        get { return _pivot ?? (_pivot = new GameObject("Pivot"));}
    }

    private GameObject _pivot;

    public Vector3 PivotEuler;

    public float LastAimedAtTime { get; private set; }

    internal bool AimLimitOn;
    internal float AimLimitYLow;
    internal float AimLimitXLow;
    internal float AimLimitXHigh;

    public Canvas VRInterfaceCanvas;
    public GameObject VRInterfaceCursor;
    public SpriteRenderer VRInterfaceCursorRenderer;

    public GraphicRaycaster Raycaster;

    public Raycastable[] Raycastables;

    [System.Serializable]
    public struct Raycastable
    {
        public Selectable Selectable;
        public Image Handle;
    }

    public void OnEnable()
    {
        #pragma warning disable 0168
        // ReSharper disable once UnusedVariable
        var svr = SteamVR.instance; // Init the SteamVR drivers
        #pragma warning restore 0168
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;
        // Cache the default value on start
        _companionEuler = gameObject.transform.localRotation.eulerAngles;
        _objectPosition = Vector3.zero;
        _relativeAlpha = RelativeAlpha;
        _relativeScale = RelativeScale;
        _attachMode = AttachMode;
        //AutoUpdateRenderTextures = false;
        var error = overlay.CreateOverlay(HOTK_Overlay.Key + gameObject.GetInstanceID(), gameObject.name, ref _handle);
        #pragma warning disable 0168
        // ReSharper disable once UnusedVariable
        var rt = RotationTracker; // Spawn RotationTracker
        #pragma warning restore 0168
        if (error != EVROverlayError.None)
        {
            Debug.LogError(error.ToString());
            enabled = false;
            return;
        }

        if (!_subscribed && Overlay != null)
        {
            _subscribed = true;
            HOTK_TrackedDeviceManager.OnControllerTriggerDown += ClickOverlay;
            HOTK_TrackedDeviceManager.OnControllerTriggerHold += ClickOverlay;
            HOTK_TrackedDeviceManager.OnControllerTriggerUp += UnclickOverlay;

            Overlay.OnOverlayAttachmentChanges += AttachToOverlay;
            Overlay.OnOverlayAlphaChanges += OverlayAlphaChanges;
            Overlay.OnOverlayScaleChanges += OverlayScaleChanges;
            Overlay.OnOverlayAspectChanges += OverlayAspectChanges;
            Overlay.OnOverlayPositionChanges += OverlayPositionChanges;
            Overlay.OnOverlayRotationChanges += OverlayRotationChanges;

            OnControllerHitsOverlay += AimAtCompanion;
            OnControllerUnhitsOverlay += UnAimAtCompanion;
        }
        _overlayMode = OverlayMode;

        HOTK_TrackedDeviceManager.Instance.SetOverlayCanAim(this, _overlayMode == CompanionMode.VRInterface);
        updateCompanion = true;
    }

    private void ClickOverlay(HOTK_TrackedDevice tracker, Point p)
    {
        ClickOverlay(tracker);
    }

    private Selectable _clickedSelectable;
    private Slider _draggingSlider;
    private float _sliderVal;

    private void ClickOverlay(HOTK_TrackedDevice tracker)
    {
        if (_overlayMode != CompanionMode.VRInterface) return;
        if (!_aiming) return;
        if (_aimedSelectable == null) return;
        var data = new PointerEventData(EventSystem.current) { position = new Vector2(_aimingX, _aimingY), dragging = true, button = PointerEventData.InputButton.Left};
        if (_clickedSelectable == null)
        {
            if (!_aimedSelectable.interactable) return;
            _clickedSelectable = _aimedSelectable;
            _clickedSelectable.OnPointerDown(data);
            var s = _clickedSelectable as Slider;
            if (s == null) return;
            _draggingSlider = s;
            _sliderVal = s.value;
        }
        else
        {
            if (_draggingSlider == null) return;
            _draggingSlider.OnDrag(data);
            if (_draggingSlider.value == _sliderVal) return;
            _sliderVal = _draggingSlider.value;
        }
    }

    private void UnclickOverlay(HOTK_TrackedDevice tracker)
    {
        if (_overlayMode != CompanionMode.VRInterface) return;
        if (_clickedSelectable == null) return;
        var data = new PointerEventData(EventSystem.current) {position = new Vector2(_aimingX, _aimingY)};
        _clickedSelectable.OnPointerUp(data);
        if (_clickedSelectable == _aimedSelectable)
        {
            var b = _clickedSelectable as Button;
            if (b != null)
            {
                if (b.interactable) b.onClick.Invoke();
            }
            else
            {
                var t = _clickedSelectable as Toggle;
                if (t != null)
                {
                    if (t.interactable)
                    {
                        t.OnPointerClick(data);
                    }
                }
            }
        }
        _clickedSelectable = null;
        _draggingSlider = null;
    }

    private bool _aiming;
    private float _aimingX;
    private float _aimingY;

    private readonly List<Selectable> _aimedSelectables = new List<Selectable>();
    Selectable _aimedSelectable = null;

    private void AimAtCompanion(HOTK_OverlayBase o, HOTK_TrackedDevice tracker, SteamVR_Overlay.IntersectionResults result)
    {
        if (VRInterfaceCanvas == null || VRInterfaceCursor == null) return;
        var lx = (VRInterfaceCanvas.pixelRect.width * result.UVs.x);
        var ly = (VRInterfaceCanvas.pixelRect.height * result.UVs.y);
        var x = -(VRInterfaceCanvas.pixelRect.width / 2f) + lx;
        var y = (VRInterfaceCanvas.pixelRect.height / 2f) - ly;

        if (AimLimitOn)
        {
            if (y > AimLimitYLow && x > AimLimitXLow && x < AimLimitXHigh)
            {
                DoAimAction(x, y, tracker);
            }
            else
            {
                StartCoroutine(FadeOutCursor());
                _aiming = false;
            }
        }
        else
        {
            DoAimAction(x, y, tracker);
        }

        if (_aimingX == x && _aimingY == y) return;
        _aimingX = x;
        _aimingY = y;

        if (!_aiming || _draggingSlider != null) return;

        var data = new PointerEventData(EventSystem.current) {position = new Vector2(x, y)};

        _aimedSelectable = null;

        foreach (var r in Raycastables.Where(r => r.Selectable.interactable))
        {
            var re = r.Handle != null ? (RectTransform)r.Handle.transform : (RectTransform)r.Selectable.transform;
            if (re == null) continue;
            if (x < (re.position.x + re.rect.xMin) ||
                x > (re.position.x + re.rect.xMax) ||
                y < (re.position.y + re.rect.yMin) ||
                y > (re.position.y + re.rect.yMax)) continue;
            if (!_aimedSelectables.Contains(r.Selectable))
            {
                _aimedSelectables.Add(r.Selectable);
                r.Selectable.OnPointerEnter(data);
                if (DesktopPortalController.Instance.HapticsEnabledToggle.isOn) tracker.TriggerHapticPulse(DesktopPortalController.HitOverlayHapticStrength);
            }
            _aimedSelectable = r.Selectable;
            break;
        }

        foreach (var b in _aimedSelectables.Where(b => _aimedSelectable == null || b != _aimedSelectable).Where(b => b.interactable))
        {
            b.OnPointerExit(data);
        }

        _aimedSelectables.Clear();
        if (_aimedSelectable != null)
            _aimedSelectables.Add(_aimedSelectable);
    }

    internal void DoAimAction(float x, float y, HOTK_TrackedDevice tracker)
    {
        VRInterfaceCursor.transform.localPosition = new Vector3(x, y, -10f);
        StartCoroutine(FadeInCursor());
        LastAimedAtTime = Time.time;
        if (!_aiming)
        {
            if (DesktopPortalController.Instance.HapticsEnabledToggle.isOn) tracker.TriggerHapticPulse(DesktopPortalController.HitOverlayHapticStrength);
        }
        _aiming = true;
    }

    private void UnAimAtCompanion(HOTK_OverlayBase o, HOTK_TrackedDevice tracker)
    {
        if (!_aiming) return;

        _aimedSelectable = null;

        _aiming = false;
        _aimingX = -1f;
        _aimingY = -1f;
        StartCoroutine(FadeOutCursor());

    }

    private IEnumerator FadeInCursor()
    {
        StopCoroutine("FadeOutCursor");
        if (VRInterfaceCursorRenderer == null || VRInterfaceCursor == null) yield break;
        var t = VRInterfaceCursorRenderer.color.a;
        ShowCursor();
        while (t < 1f)
        {
            t += 0.1f;
            if (VRInterfaceCursorRenderer != null) VRInterfaceCursorRenderer.color = new Color(VRInterfaceCursorRenderer.color.r, VRInterfaceCursorRenderer.color.g, VRInterfaceCursorRenderer.color.b, t);
            yield return new WaitForSeconds(0.025f);
        }
    }

    private IEnumerator FadeOutCursor()
    {
        StopCoroutine("FadeInCursor");
        if (VRInterfaceCursorRenderer == null || VRInterfaceCursor == null) yield break;
        var t = VRInterfaceCursorRenderer.color.a;
        while (t > 0)
        {
            t -= 0.1f;
            if (VRInterfaceCursorRenderer != null) VRInterfaceCursorRenderer.color = new Color(VRInterfaceCursorRenderer.color.r, VRInterfaceCursorRenderer.color.g, VRInterfaceCursorRenderer.color.b, t);
            yield return new WaitForSeconds(0.025f);
        }
        HideCursor();
    }

    private void ShowCursor()
    {
        if (VRInterfaceCursor.activeSelf) return;
        VRInterfaceCursor.SetActive(true);
    }
    private void HideCursor()
    {
        if (!VRInterfaceCursor.activeSelf) return;
        VRInterfaceCursor.SetActive(false);
    }

    /// <summary>
    /// When disabled, Destroy the Overlay.
    /// </summary>
    public void OnDisable()
    {
        if (_handle == OpenVR.k_ulOverlayHandleInvalid) return;
        var overlay = OpenVR.Overlay;
        if (overlay != null) overlay.DestroyOverlay(_handle);
        _handle = OpenVR.k_ulOverlayHandleInvalid;

        HOTK_TrackedDeviceManager.Instance.SetOverlayCanAim(this, false);
    }

    public void OnParentEnabled(HOTK_Overlay o)
    {
        gameObject.SetActive(OverlayMode != CompanionMode.DodgeOnGaze || Overlay.AnimateOnGaze == HOTK_Overlay.AnimationType.DodgeGaze);
    }

    public void OnParentDisabled(HOTK_Overlay o)
    {
        gameObject.SetActive(false);
    }

    private bool updateCompanion;

    public void Update()
    {
        CheckCompanionModeChanged();
        // Check if our Overlay's Texture has changed
        CheckOverlayTextureChanged();
        // Check if our Overlay's Anchor has changed
        CheckOverlayAttachmentChanged();
        // Check if our Overlay's rotation or position changed
        CheckOverlayRotationChanged();
        CheckOverlayPositionChanged();
        // Check if our Overlay's Alpha or Scale changed
        CheckOverlayAlpha();
        CheckOverlayScale();
        // Check if our Overlay is being Gazed at, or has been recently and is still animating
        //if (AnimateOnGaze != AnimationType.None) UpdateGaze(ref changed);
        // Check if a controller is aiming at our Overlay
        //UpdateControllers();
        // Check if our Overlay's HighQuality, AntiAlias, or Curved setting changed
        //CheckHighQualityChanged(ref changed);
        // Update our Overlay if anything has changed
        if (updateCompanion)// || _doUpdate)
        {
            updateCompanion = false;
            //_justUpdated = true;
            //_doUpdate = false;
            UpdateOverlay();
        }
        else
        {
            //_justUpdated = false;
            //UpdateTexture();
        }
    }

    public void DoUpdateOverlay()
    {
        updateCompanion = true;
    }

    private void CheckCompanionModeChanged()
    {
        if (_overlayMode == OverlayMode) return;
        _overlayMode = OverlayMode;

        HOTK_TrackedDeviceManager.Instance.SetOverlayCanAim(this, _overlayMode == CompanionMode.VRInterface);

        if (Overlay != null)
            AttachToOverlay(Overlay);
    }

    private void CheckOverlayTextureChanged()
    {
        if (OverlayTexture is RenderTexture || OverlayTexture is MovieTexture)
        {
            if (!AutoUpdateRenderTextures && _overlayTexture == OverlayTexture && _uvOffset == Overlay.UvOffset) return;
        }
        else if (_overlayTexture == OverlayTexture && _uvOffset == Overlay.UvOffset) return;
        _overlayTexture = OverlayTexture;
        _uvOffset = Overlay.UvOffset;
        updateCompanion = true;

        //if (MeshRenderer != null) // If our texture changes, change our MeshRenderer's texture also. The MeshRenderer is optional.
        //MeshRenderer.material.mainTexture = OverlayTexture;
    }

    private void CheckOverlayAttachmentChanged()
    {
        if (_attachMode == AttachMode) return;
        _attachMode = AttachMode;
        gameObject.transform.parent = Overlay.transform;
        gameObject.transform.localPosition = _companionOffset;
        gameObject.transform.localRotation = Quaternion.identity;
        AttachToOverlay(Overlay);
    }

    private void CheckOverlayPositionChanged()
    {
        if (_objectPosition == gameObject.transform.localPosition && _companionOffset == CompanionOffset) return;
        switch (OverlayMode)
        {
            case CompanionMode.Backside:
                gameObject.transform.localPosition = Vector3.zero;
                break;
            case CompanionMode.VRInterface:
                switch (_attachMode)
                {
                    case InterfaceAttachMode.Free:
                        gameObject.transform.localPosition = CompanionOffset;
                        break;
                    case InterfaceAttachMode.PivotTop:
                    case InterfaceAttachMode.PivotRight:
                    case InterfaceAttachMode.PivotBottom:
                    case InterfaceAttachMode.PivotLeft:
                        gameObject.transform.localPosition = CompanionOffset + _pivotOffset;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
                break;
        }
        _objectPosition = gameObject.transform.localPosition;
        _companionOffset = CompanionOffset;
        updateCompanion = true;
    }

    private void CheckOverlayRotationChanged()
    {
        if (_objectRotation == gameObject.transform.localRotation) return;
        _companionEuler = gameObject.transform.localRotation.eulerAngles;
        OverlayRotationChanges();
    }

    private void CheckOverlayAlpha()
    {
        if (_relativeAlpha == RelativeAlpha) return;
        _relativeAlpha = RelativeAlpha;

        var overlay = OpenVR.Overlay;
        if (overlay == null || !GetOverlay()) return;

        overlay.SetOverlayAlpha(_handle, _alpha * _relativeAlpha);
    }

    private void CheckOverlayScale()
    {
        if (_relativeScale == RelativeScale) return;
        _relativeScale = RelativeScale;

        var overlay = OpenVR.Overlay;
        if (overlay == null || !GetOverlay()) return;
        
        overlay.SetOverlayWidthInMeters(_handle, _scale * _relativeScale);
    }

    public override void UpdateGaze(bool wasHit)
    {
        if (OnOverlayGazed != null)
            OnOverlayGazed(this, wasHit);
    }

    /// <summary>
    /// Update the Overlay's Position and return the resulting HmdMatrix34_t
    /// </summary>
    /// <returns></returns>
    private HmdMatrix34_t GetOverlayPosition()
    {
        if (_anchor == OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            //Debug.Log(OverlayReference.transform.position + " " + transform.position);
            var offset = new SteamVR_Utils.RigidTransform(OverlayReference.transform, transform);
            offset.pos.x /= OverlayReference.transform.localScale.x;
            offset.pos.y /= OverlayReference.transform.localScale.y;
            offset.pos.z /= OverlayReference.transform.localScale.z;
            var t = offset.ToHmdMatrix34();
            return t;
        }
        else
        {
            var offset = new SteamVR_Utils.RigidTransform(HOTK_Overlay.ZeroReference.transform, OverlayReference.transform);
            offset.pos.x /= HOTK_Overlay.ZeroReference.transform.localScale.x;
            offset.pos.y /= HOTK_Overlay.ZeroReference.transform.localScale.y;
            offset.pos.z /= HOTK_Overlay.ZeroReference.transform.localScale.z;
            var t = offset.ToHmdMatrix34();
            return t;
        }
    }

    public override float GetCurrentScale()
    {
        return _scale;
    }
    public override float GetCurrentAspect()
    {
        return (float)OverlayTexture.height / (float)OverlayTexture.width;
    }

    private void AttachToOverlay(HOTK_OverlayBase o)
    {
        var overlay = o as HOTK_Overlay;
        if (overlay == null) return;
        // Update Overlay Anchor position
        GetOverlayPosition();

        // Update cached values
        _anchorDevice = overlay.AnchorDevice;
        _anchorPoint = overlay.AnchorPoint;
        _anchorOffset = overlay.AnchorOffset;
        _alpha = overlay.GetCurrentAlpha();
        _scale = overlay.GetCurrentScale();
        OverlayReference.transform.parent = Overlay.OverlayReference.transform;
        OverlayReference.transform.localPosition = Vector3.zero;
        // Attach Overlay
        switch (_anchorDevice)
        {
            case HOTK_Overlay.AttachmentDevice.Screen:
                _anchor = OpenVR.k_unTrackedDeviceIndexInvalid;
                gameObject.transform.localRotation = OverlayMode == CompanionMode.Backside ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.Euler(_companionEuler.x, _companionEuler.y, _companionEuler.z);
                OverlayReference.transform.localRotation = Quaternion.identity;
                break;
            case HOTK_Overlay.AttachmentDevice.World:
                _anchor = OpenVR.k_unTrackedDeviceIndexInvalid;
                gameObject.transform.parent = OverlayMode == CompanionMode.DodgeOnGaze ? o.gameObject.transform.parent : o.gameObject.transform;
                gameObject.transform.localPosition = OverlayMode == CompanionMode.DodgeOnGaze ? o.gameObject.transform.localPosition : Vector3.zero;
                gameObject.transform.localRotation = OverlayMode == CompanionMode.Backside ? Quaternion.AngleAxis(180f, Vector3.up) : OverlayMode == CompanionMode.DodgeOnGaze ? o.gameObject.transform.localRotation : Quaternion.Euler(_companionEuler.x, _companionEuler.y, _companionEuler.z);
                OverlayReference.transform.localRotation = Quaternion.identity;
                break;
            case HOTK_Overlay.AttachmentDevice.LeftController:
                _anchor = HOTK_TrackedDeviceManager.Instance.LeftIndex;
                gameObject.transform.localRotation = Quaternion.identity;
                OverlayReference.transform.localRotation = OverlayMode == CompanionMode.Backside ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.Euler(_companionEuler.x, _companionEuler.y, _companionEuler.z);
                OverlayRotationChanges(); // Force rotational update
                break;
            case HOTK_Overlay.AttachmentDevice.RightController:
                _anchor = HOTK_TrackedDeviceManager.Instance.RightIndex;
                gameObject.transform.localRotation = Quaternion.identity;
                OverlayReference.transform.localRotation = OverlayMode == CompanionMode.Backside ? Quaternion.AngleAxis(180f, Vector3.up) : Quaternion.Euler(_companionEuler.x, _companionEuler.y, _companionEuler.z);
                OverlayRotationChanges(); // Force rotational update
                break;
            default:
                throw new ArgumentOutOfRangeException("device", _anchorDevice, null);
        }
        if (OverlayMode == CompanionMode.VRInterface)
            switch (_attachMode)
            {
                case InterfaceAttachMode.Free:
                    gameObject.transform.parent = o.gameObject.transform;
                    gameObject.transform.localPosition = _companionOffset;
                    Pivot.hideFlags = HideFlags.HideInHierarchy;
                    Pivot.SetActive(false);
                    break;
                case InterfaceAttachMode.PivotTop:
                case InterfaceAttachMode.PivotRight:
                case InterfaceAttachMode.PivotBottom:
                case InterfaceAttachMode.PivotLeft:
                    SetupPivot(overlay);
                    SetPivotOffset(overlay);
                    AttachToPivot(overlay);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

        if (OnOverlayAttachmentChanges != null)
            OnOverlayAttachmentChanges(this);

        updateCompanion = true;
    }

    private void SetupPivot(HOTK_Overlay o)
    {
        Pivot.hideFlags = HideFlags.None;
        Pivot.SetActive(true);
        PivotEuler = Pivot.transform.localEulerAngles;
        switch (o.AnchorDevice)
        {
            case HOTK_Overlay.AttachmentDevice.World:
            case HOTK_Overlay.AttachmentDevice.Screen:
                Pivot.transform.parent = o.gameObject.transform;
                break;
            case HOTK_Overlay.AttachmentDevice.LeftController:
            case HOTK_Overlay.AttachmentDevice.RightController:
                Pivot.transform.parent = o.OverlayReference.transform;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        Pivot.transform.localRotation = Quaternion.Euler(PivotEuler);
    }

    private void SetPivotOffset(HOTK_Overlay overlay)
    {
        switch (_attachMode)
        {
            case InterfaceAttachMode.Free:
                _pivotOffset = Vector3.zero;
                break;
            case InterfaceAttachMode.PivotTop:
                Pivot.transform.localPosition = new Vector3(0f, -(overlay.GetCurrentHeight()/2f), 0f);
                _pivotOffset = new Vector3(0f, -(GetCurrentHeight()/2f), 0f);
                break;
            case InterfaceAttachMode.PivotRight:
                Pivot.transform.localPosition = new Vector3((overlay.GetCurrentWidth()/2f), 0f, 0f);
                _pivotOffset = new Vector3((GetCurrentWidth()/2f), 0f, 0f);
                break;
            case InterfaceAttachMode.PivotBottom:
                Pivot.transform.localPosition = new Vector3(0f, (overlay.GetCurrentHeight()/2f), 0f);
                _pivotOffset = new Vector3(0f, (GetCurrentHeight()/2f), 0f);
                break;
            case InterfaceAttachMode.PivotLeft:
                Pivot.transform.localPosition = new Vector3(-(overlay.GetCurrentWidth()/2f), 0f, 0f);
                _pivotOffset = new Vector3(-(GetCurrentWidth()/2f), 0f, 0f);
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        //Pivot.transform.localRotation = Quaternion.Euler(PivotEuler);
    }

    private void AttachToPivot(HOTK_Overlay o)
    {
        switch (o.AnchorDevice)
        {
            case HOTK_Overlay.AttachmentDevice.World:
            case HOTK_Overlay.AttachmentDevice.Screen:
                gameObject.transform.parent = Pivot.transform;
                gameObject.transform.localPosition = _companionOffset + _pivotOffset;
                gameObject.transform.localRotation = Quaternion.identity;
                break;
            case HOTK_Overlay.AttachmentDevice.LeftController:
            case HOTK_Overlay.AttachmentDevice.RightController:
                OverlayReference.transform.parent = Pivot.transform;
                OverlayReference.transform.localPosition = _companionOffset + _pivotOffset;
                OverlayReference.transform.localRotation = Quaternion.identity;
                gameObject.transform.parent = o.gameObject.transform;
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private void OverlayAlphaChanges(HOTK_Overlay o, float alpha)
    {
        _alpha = alpha;
        updateCompanion = true;
    }

    private void OverlayScaleChanges(HOTK_Overlay o, float scale)
    {
        _scale = scale;
        SetPivotOffset(o);
        if (OverlayMode == CompanionMode.VRInterface)
            switch (_attachMode)
            {
                case InterfaceAttachMode.Free:
                    gameObject.transform.localPosition = _companionOffset;
                    break;
                case InterfaceAttachMode.PivotTop:
                case InterfaceAttachMode.PivotRight:
                case InterfaceAttachMode.PivotBottom:
                case InterfaceAttachMode.PivotLeft:
                    gameObject.transform.localPosition = _companionOffset + _pivotOffset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        updateCompanion = true;
    }

    private void OverlayAspectChanges(HOTK_Overlay o, float aspect)
    {
        SetPivotOffset(o);
        if (OverlayMode == CompanionMode.VRInterface)
            switch (_attachMode)
            {
                case InterfaceAttachMode.Free:
                    gameObject.transform.localPosition = _companionOffset;
                    break;
                case InterfaceAttachMode.PivotTop:
                case InterfaceAttachMode.PivotRight:
                case InterfaceAttachMode.PivotBottom:
                case InterfaceAttachMode.PivotLeft:
                    gameObject.transform.localPosition = _companionOffset + _pivotOffset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        updateCompanion = true;
    }

    private void OverlayPositionChanges(HOTK_Overlay o, Vector3 pos)
    {
        if (OverlayMode != CompanionMode.DodgeOnGaze) return;
        if (_anchorDevice == HOTK_Overlay.AttachmentDevice.World)
            gameObject.transform.localPosition = pos;
        updateCompanion = true;
    }

    private void OverlayRotationChanges(HOTK_Overlay o, Quaternion rot)
    {
        if (OverlayMode == CompanionMode.DodgeOnGaze)
        {
            if (_anchorDevice == HOTK_Overlay.AttachmentDevice.World)
                gameObject.transform.localRotation = rot;
        }
        OverlayRotationChanges();
    }

    private void OverlayRotationChanges()
    {
        _objectRotation = gameObject.transform.localRotation;
        updateCompanion = true;
    }

    private bool GetOverlay()
    {
        var overlay = OpenVR.Overlay;
        if (overlay == null) return false;

        var error = overlay.ShowOverlay(_handle);
        if (error == EVROverlayError.InvalidHandle || error == EVROverlayError.UnknownOverlay)
        {
            if (overlay.FindOverlay(HOTK_Overlay.Key + gameObject.GetInstanceID(), ref _handle) != EVROverlayError.None) return false;
        }

        return true;
    }

    /// <summary>
    /// Push Updates to our Overlay to the OpenVR System
    /// </summary>
    private void UpdateOverlay()
    {
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;

        if (OverlayTexture != null)
        {
            if (!GetOverlay()) return;

            var tex = new Texture_t
            {
                handle = OverlayTexture.GetNativeTexturePtr(), eType = SteamVR.instance.graphicsAPI, eColorSpace = EColorSpace.Auto
            };
            overlay.SetOverlayColor(_handle, 1f, 1f, 1f);
            //overlay.SetOverlayGamma(_handle, 2.2f); // Doesn't exist yet :(
            overlay.SetOverlayTexture(_handle, ref tex);
            overlay.SetOverlayAlpha(_handle, _alpha*_relativeAlpha);
            overlay.SetOverlayWidthInMeters(_handle, _scale*_relativeScale);

            var textureBounds = new VRTextureBounds_t
            {
                uMin = (0 + Overlay.UvOffset.x)*Overlay.UvOffset.z, vMin = (1 + Overlay.UvOffset.y)*Overlay.UvOffset.w, uMax = (1 + Overlay.UvOffset.x)*Overlay.UvOffset.z, vMax = (0 + Overlay.UvOffset.y)*Overlay.UvOffset.w
            };
            overlay.SetOverlayTextureBounds(_handle, ref textureBounds);

            var vecMouseScale = new HmdVector2_t
            {
                v0 = 1f, v1 = (float) OverlayTexture.height/(float) OverlayTexture.width
            };
            overlay.SetOverlayMouseScale(_handle, ref vecMouseScale);

            if (_anchor != OpenVR.k_unTrackedDeviceIndexInvalid) // Attached to some HOTK_TrackedDevice, used for Controllers
            {
                var t = GetOverlayPosition();
                overlay.SetOverlayTransformTrackedDeviceRelative(_handle, _anchor, ref t);
            }
            else if (_anchorDevice == HOTK_Overlay.AttachmentDevice.World) // Attached to World
            {
                var t = GetOverlayPosition();
                overlay.SetOverlayTransformAbsolute(_handle, SteamVR_Render.instance.trackingSpace, ref t);
            }
            else
            {
                var vrcam = SteamVR_Render.Top();
                if (vrcam != null && vrcam.origin != null) // Attached to Camera (We are Rendering)
                {
                    var offset = new SteamVR_Utils.RigidTransform(vrcam.origin, transform);
                    offset.pos.x /= vrcam.origin.localScale.x;
                    offset.pos.y /= vrcam.origin.localScale.y;
                    offset.pos.z /= vrcam.origin.localScale.z;

                    var t = offset.ToHmdMatrix34();
                    overlay.SetOverlayTransformAbsolute(_handle, SteamVR_Render.instance.trackingSpace, ref t);
                }
                else // Attached to Camera (We are Not Rendering)
                {
                    var t = GetOverlayPosition();
                    overlay.SetOverlayTransformTrackedDeviceRelative(_handle, 0, ref t);
                }
            }

            overlay.SetOverlayInputMethod(_handle, InputMethod);
        }
        else
        {
            overlay.HideOverlay(_handle);
        }
    }

    public enum CompanionMode
    {
        Backside,
        VRInterface,
        DodgeOnGaze
    }

    public enum InterfaceAttachMode
    {
        Free,
        PivotTop,
        PivotRight,
        PivotBottom,
        PivotLeft
    }
}
