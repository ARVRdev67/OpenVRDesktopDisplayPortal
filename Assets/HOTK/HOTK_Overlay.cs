using System;
using System.Collections.Generic;
using UnityEngine;
using Valve.VR;
using Random = System.Random;

public class HOTK_Overlay : HOTK_OverlayBase
{
    #region Custom Inspector Vars
    [NonSerialized] public bool ShowSettingsAppearance = true;
    [NonSerialized] public bool ShowSettingsInput = false;
    [NonSerialized] public bool ShowSettingsAttachment = false;
    #endregion

    #region Settings
    [Tooltip("The texture that will be drawn for the Overlay.")]
    public Texture OverlayTexture;
    [Tooltip("How, if at all, the Overlay is animated when being looked at.")]
    public AnimationType AnimateOnGaze = AnimationType.None;
    private AnimationType _animateOnGaze;
    [Tooltip("The alpha at which the Overlay will be drawn.")]
    public float Alpha = 1.0f;			// opacity 0..1
    [Tooltip("The alpha at which the Overlay will be drawn.")]
    public float Alpha2 = 1.0f;			// opacity 0..1 - Only used for AnimateOnGaze
    [Tooltip("The speed the Alpha changes at.")]
    public float AlphaSpeed = 0.01f;
    [Tooltip("The scale at which the Overlay will be drawn.")]
    public float Scale = 1.0f;			// size of overlay view
    [Tooltip("The scale at which the Overlay will be drawn.")]
    public float Scale2 = 1.0f;			// size of overlay view - Only used for AnimateOnGaze
    [Tooltip("The speed the Scale changes at.")]
    public float ScaleSpeed = 0.1f;
    [Tooltip("This causes the Overlay to draw directly to the screen, instead of to the VRCompositor.")]
    public bool Highquality;            // Only one Overlay can be HQ at a time
    [Tooltip("This causes the Overlay to draw with Anti-Aliasing. Requires High Quality.")]
    public bool Antialias;
    [Tooltip("This causes the Overlay to draw curved. Requires High Quality.")]
    public bool Curved;

    public Vector4 UvOffset = new Vector4(0, 0, 1, 1);
    public Vector2 MouseScale = Vector3.one;
    public Vector2 CurvedRange = new Vector2(1, 2);
    public VROverlayInputMethod InputMethod = VROverlayInputMethod.None;
    public Vector2 DodgeGazeOffset = Vector2.zero;
    public float DodgeGazeSpeed = 0.1f;

    [Tooltip("Controls where the Overlay will be drawn.")]
    public AttachmentDevice AnchorDevice = AttachmentDevice.Screen;
    [Tooltip("Controls the base offset for the Overlay.")]
    public AttachmentPoint AnchorPoint = AttachmentPoint.Center;
    [Tooltip("Controls the offset for the Overlay.")]
    public Vector3 AnchorOffset = Vector3.zero;
    public FramerateMode Framerate = FramerateMode._30FPS;
    #endregion

    public Action<HOTK_Overlay> OnOverlayEnabled;
    public Action<HOTK_Overlay> OnOverlayDisabled;
    public Action<HOTK_Overlay> OnOverlayAnimationChanges;
    public Action<HOTK_Overlay, Vector3> OnOverlayPositionChanges;
    public Action<HOTK_Overlay, Quaternion> OnOverlayRotationChanges;
    public Action<HOTK_Overlay, float> OnOverlayAlphaChanges;
    public Action<HOTK_Overlay, float> OnOverlayScaleChanges;
    public Action<HOTK_Overlay, float> OnOverlayAspectChanges;
    public Action<HOTK_Overlay, AttachmentDevice> OnOverlayAnchorChanges;
    public Action<HOTK_Overlay, Quaternion> OnOverlayAnchorRotationChanges;

    #region Interal Vars
    public static Random Rand = new Random();
    public static HOTK_Overlay HighQualityOverlay;  // Only one Overlay can be HQ at a time
    public static string Key { get { return "unity:" + Application.companyName + "." + Application.productName + "." + Rand.Next(); } }
    public static GameObject ZeroReference;         // Used to get a reference to the world 0, 0, 0 point
    public bool IsBeingGazed;

    public bool IsDodging
    {
        get { return _dodging; }
    }

    private bool _wasHighQuality;
    private bool _wasAntiAlias;
    private bool _wasCurved;
    private uint _anchor;   // caches a HOTK_TrackedDevice ID for anchoring the Overlay, if applicable

    private float _alpha
    {
        get { return _actualAlpha; }
        set
        {
            _actualAlpha = value;
            if (OnOverlayAlphaChanges != null)
                OnOverlayAlphaChanges(this, value);
        }
    }

    private float _scale
    {
        get { return _actualScale; }
        set
        {
            _actualScale = value;
            if (OnOverlayScaleChanges != null)
                OnOverlayScaleChanges(this, value);
        }
    }

    private float _actualAlpha;
    private float _actualScale;

    public bool GazeLocked
    {
        get { return _lockGaze; }
    }

    public bool GazeLockedOn
    {
        get { return _lockedGaze; }
    }

    private bool _lockGaze; // If true, _lockedGaze will be used instead of actually testing for gaze
    private bool _lockedGaze; // If _lockGaze, this value is forced instead of testing for gaze

    // Caches our MeshRenderer, if applicable
    private MeshRenderer MeshRenderer
    {
        get { return _meshRenderer ?? (_meshRenderer = GetComponent<MeshRenderer>()); }
    }
    private MeshRenderer _meshRenderer;

    private bool _justUpdated;
    private bool _doUpdate;
    #endregion

    public void LockGaze(bool lockedOn)
    {
        _lockGaze = true;
        _lockedGaze = lockedOn;
    }
    public void UnlockGaze()
    {
        _lockGaze = false;
    }

    public void DoUpdate()
    {
        _doUpdate = true;
    }
    
    /// <summary>
    /// Check if anything has changed with the Overlay, and update the OpenVR system as necessary.
    /// </summary>
    public void Update()
    {
        var changed = false;
        CheckAnimationChanged(ref changed);
        // Check if our Overlay's Texture has changed
        CheckOverlayTextureChanged(ref changed);
        // Check if our Overlay's Anchor has changed
        CheckOverlayAnchorChanged(ref changed);
        // Check if our Overlay's rotation or position changed
        CheckOverlayRotationChanged(ref changed);
        CheckOverlayPositionChanged(ref changed);
        // Check if our Overlay's Alpha or Scale changed
        CheckOverlayAlphaAndScale(ref changed);
        // Check if our Overlay's HighQuality, AntiAlias, or Curved setting changed
        CheckHighQualityChanged(ref changed);
        // Update our Overlay if anything has changed
        if (changed || _doUpdate)
        {
            _justUpdated = true;
            _doUpdate = false;
            UpdateOverlay();
        }
        else
        {
            _justUpdated = false;
            UpdateTexture();
        }
    }

    private void CheckAnimationChanged(ref bool changed)
    {
        if (_animateOnGaze == AnimateOnGaze) return;
        StopDodging();
        _animateOnGaze = AnimateOnGaze;
        changed = true;
        if (OnOverlayAnimationChanges != null)
            OnOverlayAnimationChanges(this);
    }

    public override void UpdateGaze(bool hit)
    {
        IsBeingGazed = hit;
        HandleAnimateOnGaze(hit);
    }

    public bool ClearOverlayTexture()
    {
        var overlay = OpenVR.Overlay;
        if (overlay == null) return false;
        return (overlay.ClearOverlayTexture(_handle) == EVROverlayError.None);
    }

    public void Start()
    {
        HOTK_TrackedDeviceManager.OnControllerIndexChanged += OnControllerIndexChanged;
    }

    // If the controller we are tracking changes index, update
    private void OnControllerIndexChanged(ETrackedControllerRole role, uint index)
    {
        if (_anchorDevice == AttachmentDevice.LeftController && role == ETrackedControllerRole.LeftHand)
        {
            _anchorDevice = AttachmentDevice.World; // This will trick the system into reattaching the overlay
        }
        else if (_anchorDevice == AttachmentDevice.RightController && role == ETrackedControllerRole.RightHand)
        {
            _anchorDevice = AttachmentDevice.World; // This will trick the system into reattaching the overlay
        }
    }

    /// <summary>
    /// When enabled, Create the Overlay and reset cached values.
    /// </summary>
    public void OnEnable()
    {
        #pragma warning disable 0168
        // ReSharper disable once UnusedVariable
        var svr = SteamVR.instance; // Init the SteamVR drivers
        #pragma warning restore 0168
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;
        // Cache the default value on start
        _scale = Scale;
        _alpha = Alpha;
        _uvOffset = UvOffset;
        _objectRotation = Quaternion.identity;
        _objectPosition = Vector3.zero;
        AutoUpdateRenderTextures = true;
        var error = overlay.CreateOverlay(Key + gameObject.GetInstanceID(), gameObject.name, ref _handle);
        if (error != EVROverlayError.None)
        {
            Debug.Log(error.ToString());
            enabled = false;
            return;
        }
        #pragma warning disable 0168
        // ReSharper disable once UnusedVariable
        var rt = RotationTracker; // Spawn RotationTracker
        #pragma warning restore 0168
        if (_handle != OpenVR.k_ulOverlayHandleInvalid)
        {
            HOTK_TrackedDeviceManager.Instance.SetOverlayCanGaze(this, AnimateOnGaze != AnimationType.DodgeGaze);
            HOTK_TrackedDeviceManager.Instance.SetOverlayCanAim(this);
        }
        if (OnOverlayEnabled != null)
            OnOverlayEnabled(this);
    }

    /// <summary>
    /// When disabled, Destroy the Overlay.
    /// </summary>
    public void OnDisable()
    {
        if (_handle == OpenVR.k_ulOverlayHandleInvalid) return;
        HOTK_TrackedDeviceManager.Instance.SetOverlayCanGaze(this, false);
        HOTK_TrackedDeviceManager.Instance.SetOverlayCanAim(this, false);
        var overlay = OpenVR.Overlay;
        if (overlay != null) overlay.DestroyOverlay(_handle);
        _handle = OpenVR.k_ulOverlayHandleInvalid;
        if (OnOverlayDisabled != null)
            OnOverlayDisabled(this);
    }
    
    /// <summary>
    /// Attach the Overlay to [device] at base position [point].
    /// [point] isn't used for HMD or World, and can be ignored.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="point"></param>
    public void AttachTo(AttachmentDevice device, AttachmentPoint point = AttachmentPoint.Center)
    {
        AttachTo(device, 1f, Vector3.zero, point);
    }
    /// <summary>
    /// Attach the Overlay to [device] at [scale], and base position [point].
    /// [point] isn't used for HMD or World, and can be ignored.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="scale"></param>
    /// <param name="point"></param>
    public void AttachTo(AttachmentDevice device, float scale, AttachmentPoint point = AttachmentPoint.Center)
    {
        AttachTo(device, scale, Vector3.zero, point);
    }
    /// <summary>
    /// Attach the Overlay to [device] at [scale] size with offset [offset], and base position [point].
    /// [point] isn't used for HMD or World, and can be ignored.
    /// </summary>
    /// <param name="device"></param>
    /// <param name="scale"></param>
    /// <param name="offset"></param>
    /// <param name="point"></param>
    public void AttachTo(AttachmentDevice device, float scale, Vector3 offset, AttachmentPoint point = AttachmentPoint.Center)
    {
        StopDodging();

        // Update Overlay Anchor position
        GetOverlayPosition();

        if (_anchorDevice != device && OnOverlayAnchorChanges != null)
            OnOverlayAnchorChanges(this, device);

        // Update cached values
        _anchorDevice = device;
        AnchorDevice = device;
        _anchorPoint = point;
        AnchorPoint = point;
        _anchorOffset = offset;
        AnchorOffset = offset;
        Scale = scale;

        if (OnOverlayAnchorRotationChanges != null)
            OnOverlayAnchorRotationChanges(this, Quaternion.identity);

        // Attach Overlay
        switch (device)
        {
            case AttachmentDevice.Screen:
                _anchor = OpenVR.k_unTrackedDeviceIndexInvalid;
                gameObject.transform.localPosition = offset;
                OverlayReference.transform.localPosition = Vector3.zero;
                OverlayReference.transform.localRotation = Quaternion.identity;
                break;
            case AttachmentDevice.World:
                _anchor = OpenVR.k_unTrackedDeviceIndexInvalid;
                gameObject.transform.localPosition = offset;
                OverlayReference.transform.localPosition = Vector3.zero;
                OverlayReference.transform.localRotation = Quaternion.identity;
                break;
            case AttachmentDevice.LeftController:
                _anchor = HOTK_TrackedDeviceManager.Instance.LeftIndex;
                AttachToController(point, offset);
                break;
            case AttachmentDevice.RightController:
                _anchor = HOTK_TrackedDeviceManager.Instance.RightIndex;
                AttachToController(point, offset);
                break;
            default:
                throw new ArgumentOutOfRangeException("device", device, null);
        }

        if (OnOverlayAttachmentChanges != null)
            OnOverlayAttachmentChanges(this);
    }

    /// <summary>
    /// Update the Overlay's Position and Rotation, relative to the selected controller, attaching it to [point] with offset [offset]
    /// </summary>
    /// <param name="point"></param>
    /// <param name="offset"></param>
    private void AttachToController(AttachmentPoint point, Vector3 offset)
    {
        float dx = offset.x, dy = offset.y, dz = offset.z;
        // Offset our position based on the Attachment Point
        switch (point)
        {
            case AttachmentPoint.Center:
                break;
            case AttachmentPoint.FlatAbove:
                dz += 0.05f;
                break;
            case AttachmentPoint.FlatBelow:
                dz -= 0.18f;
                break;
            case AttachmentPoint.FlatBelowFlipped:
                dz += 0.18f;
                break;
            case AttachmentPoint.Above:
                dz -= 0.01f;
                break;
            case AttachmentPoint.AboveFlipped:
                dz += 0.01f;
                break;
            case AttachmentPoint.Below:
                dz += 0.1f;
                break;
            case AttachmentPoint.BelowFlipped:
                dz -= 0.1f;
                break;
            case AttachmentPoint.Up:
                dy += 0.5f;
                break;
            case AttachmentPoint.Down:
                dy -= 0.5f;
                break;
            case AttachmentPoint.Left:
                dx -= 0.5f;
                break;
            case AttachmentPoint.Right:
                dx += 0.5f;
                break;
            default:
                throw new ArgumentOutOfRangeException("point", point, null);
        }

        Vector3 pos;
        var rot = Quaternion.identity;
        // Apply position and rotation to Overlay anchor
        // Some Axis are flipped here to reorient the offset
        switch (point)
        {
            case AttachmentPoint.FlatAbove:
            case AttachmentPoint.FlatBelow:
                pos = new Vector3(dx, dy, dz);
                break;
            case AttachmentPoint.FlatBelowFlipped:
                pos = new Vector3(dx, -dy, -dz);
                rot = Quaternion.AngleAxis(180f, new Vector3(1f, 0f, 0f));
                break;
            case AttachmentPoint.Center:
            case AttachmentPoint.Above:
            case AttachmentPoint.Below:
                pos = new Vector3(dx, -dz, dy);
                rot = Quaternion.AngleAxis(90f, new Vector3(1f, 0f, 0f));
                break;
            case AttachmentPoint.Up:
            case AttachmentPoint.Down:
            case AttachmentPoint.Left:
            case AttachmentPoint.Right:
                pos = new Vector3(dx, -dz, dy);
                rot = Quaternion.AngleAxis(90f, new Vector3(1f, 0f, 0f));
                break;
            case AttachmentPoint.AboveFlipped:
            case AttachmentPoint.BelowFlipped:
                pos = new Vector3(-dx, dz, dy);
                rot = Quaternion.AngleAxis(90f, new Vector3(1f, 0f, 0f)) * Quaternion.AngleAxis(180f, new Vector3(0f, 1f, 0f));
                break;
            default:
                throw new ArgumentOutOfRangeException("point", point, null);
        }
        OverlayReference.transform.localPosition = pos;
        _anchorRotation = rot;
        if (OnOverlayAnchorRotationChanges != null)
            OnOverlayAnchorRotationChanges(this, rot);
        var changed = false;
        CheckOverlayRotationChanged(ref changed, true); // Force rotational update
    }

    private void CheckOverlayAlphaAndScale(ref bool changed)
    {
        if (AnimateOnGaze != AnimationType.Alpha && AnimateOnGaze != AnimationType.AlphaAndScale)
        {
            if (_alpha != Alpha) // Loss of precision but it should work
            {
                StopDodging();
                _alpha = Alpha;
                changed = true;
            }
        }
        if (AnimateOnGaze != AnimationType.Scale && AnimateOnGaze != AnimationType.AlphaAndScale)
        {
            if (_scale != Scale) // Loss of precision but it should work
            {
                StopDodging();
                _scale = Scale;
                changed = true;
            }
        }
    }

    /// <summary>
    /// Check if our Overlay's Anchor has changed, and AttachTo it if necessary.
    /// </summary>
    /// <param name="changed"></param>
    private void CheckOverlayAnchorChanged(ref bool changed)
    {
        // If the AnchorDevice changes, or our Attachment Point or Offset changes, reattach the overlay
        if (_anchorDevice == AnchorDevice && _anchorPoint == AnchorPoint && _anchorOffset == AnchorOffset) return;
        AttachTo(AnchorDevice, Scale, AnchorOffset, AnchorPoint);
        changed = true;
    }

    /// <summary>
    /// Update the Overlay's Position if necessary.
    /// </summary>
    /// <returns></returns>
    private void CheckOverlayPositionChanged(ref bool changed)
    {
        if (AnchorDevice == AttachmentDevice.LeftController || AnchorDevice == AttachmentDevice.RightController) return; // Controller overlays do not adjust with gameObject transform
        if (_objectPosition == gameObject.transform.localPosition) return;
        _objectPosition = gameObject.transform.localPosition;
        if (OnOverlayPositionChanges != null)
            OnOverlayPositionChanges(this, _objectPosition);
        changed = true;
    }

    /// <summary>
    /// Update the Overlay's Rotation if necessary.
    /// </summary>
    /// <param name="changed"></param>
    /// <param name="force"></param>
    private void CheckOverlayRotationChanged(ref bool changed, bool force = false)
    {
        var gameObjectChanged = _objectRotation != gameObject.transform.localRotation;
        if (gameObjectChanged)
        {
            StopDodging();
            _objectRotation = gameObject.transform.localRotation;
            if (OnOverlayRotationChanges != null)
                OnOverlayRotationChanges(this, _objectRotation);
            changed = true;
        }
        if (_anchor == OpenVR.k_unTrackedDeviceIndexInvalid) return; // This part below is only for Controllers
        if (!force && !gameObjectChanged && OverlayReference.transform.localRotation == _anchorRotation * _objectRotation) return;
        OverlayReference.transform.localRotation = _anchorRotation * _objectRotation;
        changed = true;
    }

    /// <summary>
    /// Update the Overlay's Texture if necessary.
    /// </summary>
    /// <param name="changed"></param>
    private void CheckOverlayTextureChanged(ref bool changed)
    {
        if (_overlayTexture == OverlayTexture && _uvOffset == UvOffset) return;
        _overlayTexture = OverlayTexture;
        _uvOffset = UvOffset;
        changed = true;

        if (MeshRenderer != null) // If our texture changes, change our MeshRenderer's texture also. The MeshRenderer is optional.
            MeshRenderer.material.mainTexture = OverlayTexture;
    }

    private void CheckHighQualityChanged(ref bool changed)
    {
        if (_wasHighQuality == Highquality && _wasAntiAlias == Antialias && _wasCurved == Curved) return;
        _wasHighQuality = Highquality;
        _wasAntiAlias = Antialias;
        _wasCurved = Curved;
        changed = true;
    }

    /// <summary>
    /// Update the Overlay's Position and return the resulting HmdMatrix34_t
    /// </summary>
    /// <returns></returns>
    private HmdMatrix34_t GetOverlayPosition()
    {
        if (_anchor == OpenVR.k_unTrackedDeviceIndexInvalid)
        {
            var offset = new SteamVR_Utils.RigidTransform(OverlayReference.transform, transform);
            offset.pos.x /= OverlayReference.transform.localScale.x;
            offset.pos.y /= OverlayReference.transform.localScale.y;
            offset.pos.z /= OverlayReference.transform.localScale.z;
            var t = offset.ToHmdMatrix34();
            return t;
        }
        else
        {
            if (ZeroReference == null) ZeroReference = new GameObject("Zero Reference") { hideFlags = HideFlags.HideInHierarchy };
            var offset = new SteamVR_Utils.RigidTransform(ZeroReference.transform, OverlayReference.transform);
            offset.pos.x /= ZeroReference.transform.localScale.x;
            offset.pos.y /= ZeroReference.transform.localScale.y;
            offset.pos.z /= ZeroReference.transform.localScale.z;
            var t = offset.ToHmdMatrix34();
            return t;
        }
    }

    private bool _dodging;
    private Vector3 _dodgingBase;
    private Vector3 _dodgingTarget;
    private float _dodgingVal;
    private float _dodgingOffsetX;
    private float _dodgingOffsetY;
    private bool _dodgingFull;

    public void GazeDetectorGazed(HOTK_OverlayBase o, bool wasHit)
    {
        var changed = false;

        if (_dodging && (_dodgingOffsetX != DodgeGazeOffset.x || _dodgingOffsetY != DodgeGazeOffset.y))
        {
            _dodgingOffsetX = DodgeGazeOffset.x;
            _dodgingOffsetY = DodgeGazeOffset.y;
            AnchorDodge();
            if (_dodgingFull)
            {
                OverlayReference.transform.localPosition = _dodgingTarget;
                changed = true;
            }
        }

        if (wasHit)
        {
            if (!_dodging)
            {
                _dodging = true;
                _dodgingBase = OverlayReference.transform.localPosition;
                AnchorDodge();
                _dodgingVal = 0f;
            }else if (_dodgingVal < 1f)
            {
                _dodgingVal += DodgeGazeSpeed;
                if (_dodgingVal > 1f)
                    _dodgingVal = 1f;
                OverlayReference.transform.localPosition = Vector3.Lerp(_dodgingBase, _dodgingTarget, _dodgingVal);
                changed = true;
            }
            else _dodgingFull = true;
        }
        else
        {
            _dodgingFull = false;
            if (_dodging)
            {
                if (_dodgingVal > 0f)
                {
                    _dodgingVal -= DodgeGazeSpeed;
                    if (_dodgingVal < 0f)
                        _dodgingVal = 0f;
                    OverlayReference.transform.localPosition = Vector3.Lerp(_dodgingBase, _dodgingTarget, _dodgingVal);
                    changed = true;
                }
                else
                {
                    _dodging = false;
                    OverlayReference.transform.localPosition = _dodgingBase;
                }
            }
        }

        if (changed)
            DoUpdate();
    }

    private void AnchorDodge()
    {
        _dodgingOffsetX = DodgeGazeOffset.x;
        _dodgingOffsetY = DodgeGazeOffset.y;
        var aspect = (float) OverlayTexture.height/(float) OverlayTexture.width;
        if (_anchorDevice == AttachmentDevice.LeftController || _anchorDevice == AttachmentDevice.RightController)
            _dodgingTarget = _dodgingBase + (OverlayReference.transform.right * _dodgingOffsetX * Scale) + (OverlayReference.transform.up * _dodgingOffsetY * Scale * aspect);
        else _dodgingTarget = _dodgingBase - (gameObject.transform.right * _dodgingOffsetX * Scale) - (gameObject.transform.up * _dodgingOffsetY * Scale * aspect);
    }

    public void StopDodging()
    {
        if (_dodging)
        {
            OverlayReference.transform.localPosition = _dodgingBase;
            _dodging = false;
        }
    }

    /// <summary>
    /// Animate this Overlay, based on it's AnimateOnGaze setting.
    /// </summary>
    /// <param name="hit"></param>
    /// <param name="changed"></param>
    private void HandleAnimateOnGaze(bool hit)
    {
        bool changed = false;
        if (hit)
        {
            if (AnimateOnGaze == AnimationType.Alpha || AnimateOnGaze == AnimationType.AlphaAndScale)
            {
                if (_alpha < Alpha2)
                {
                    _alpha += AlphaSpeed;
                    changed = true;
                    if (_alpha > Alpha2)
                        _alpha = Alpha2;
                }else if (_alpha > Alpha2)
                {
                    _alpha -= AlphaSpeed;
                    changed = true;
                    if (_alpha < Alpha2)
                        _alpha = Alpha2;
                }
            }
            if (AnimateOnGaze == AnimationType.Scale || AnimateOnGaze == AnimationType.AlphaAndScale)
            {
                if (_scale < Scale2)
                {
                    _scale += ScaleSpeed;
                    changed = true;
                    if (_scale > Scale2)
                        _scale = Scale2;
                }else if (_scale > Scale2)
                {
                    _scale -= ScaleSpeed;
                    changed = true;
                    if (_scale < Scale2)
                        _scale = Scale2;
                }
            }
        }
        else
        {
            if (AnimateOnGaze == AnimationType.Alpha || AnimateOnGaze == AnimationType.AlphaAndScale)
            {
                if (_alpha > Alpha)
                {
                    _alpha -= AlphaSpeed;
                    changed = true;
                    if (_alpha < Alpha)
                        _alpha = Alpha;
                }else if (_alpha < Alpha)
                {
                    _alpha += AlphaSpeed;
                    changed = true;
                    if (_alpha > Alpha)
                        _alpha = Alpha;
                }
            }
            if (AnimateOnGaze == AnimationType.Scale || AnimateOnGaze == AnimationType.AlphaAndScale)
            {
                if (_scale > Scale)
                {
                    _scale -= ScaleSpeed;
                    changed = true;
                    if (_scale < Scale)
                        _scale = Scale;
                }else if (_scale < Scale)
                {
                    _scale += ScaleSpeed;
                    changed = true;
                    if (_scale > Scale)
                        _scale = Scale;
                }
            }
        }
        if (changed)
            DoUpdate();
    }

    public float GetCurrentAlpha()
    {
        return (AnimateOnGaze == AnimationType.Alpha || AnimateOnGaze == AnimationType.AlphaAndScale ? _alpha : Alpha);
    }

    public override float GetCurrentScale()
    {
        return (AnimateOnGaze == AnimationType.Scale || AnimateOnGaze == AnimationType.AlphaAndScale ? _scale : Scale);
    }
    public override float GetCurrentAspect()
    {
        return (float)OverlayTexture.height / (float)OverlayTexture.width;
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
            var error = overlay.ShowOverlay(_handle);
            if (error == EVROverlayError.InvalidHandle || error == EVROverlayError.UnknownOverlay)
            {
                if (overlay.FindOverlay(Key + gameObject.GetInstanceID(), ref _handle) != EVROverlayError.None) return;
            }

            var tex = new Texture_t
            {
                handle = OverlayTexture.GetNativeTexturePtr(),
                eType = SteamVR.instance.graphicsAPI,
                eColorSpace = EColorSpace.Auto
            };
            overlay.SetOverlayColor(_handle, 1f, 1f, 1f);
            //overlay.SetOverlayGamma(_handle, 2.2f); // Doesn't exist yet :(
            overlay.SetOverlayTexture(_handle, ref tex);
            overlay.SetOverlayAlpha(_handle, AnimateOnGaze == AnimationType.Alpha || AnimateOnGaze == AnimationType.AlphaAndScale ? _alpha : Alpha);
            overlay.SetOverlayWidthInMeters(_handle, AnimateOnGaze == AnimationType.Scale || AnimateOnGaze == AnimationType.AlphaAndScale ? _scale : Scale);
            overlay.SetOverlayAutoCurveDistanceRangeInMeters(_handle, CurvedRange.x, CurvedRange.y);

            var textureBounds = new VRTextureBounds_t
            {
                uMin = (0 + UvOffset.x) * UvOffset.z, vMin = (1 + UvOffset.y) * UvOffset.w,
                uMax = (1 + UvOffset.x) * UvOffset.z, vMax = (0 + UvOffset.y) * UvOffset.w
            };
            overlay.SetOverlayTextureBounds(_handle, ref textureBounds);
            
            var vecMouseScale = new HmdVector2_t
            {
                v0 = 1f,
                v1 = (float)OverlayTexture.height / (float)OverlayTexture.width
            };
            overlay.SetOverlayMouseScale(_handle, ref vecMouseScale);

            if (_anchor != OpenVR.k_unTrackedDeviceIndexInvalid) // Attached to some HOTK_TrackedDevice, used for Controllers
            {
                var t = GetOverlayPosition();
                overlay.SetOverlayTransformTrackedDeviceRelative(_handle, _anchor, ref t);
            }
            else if (AnchorDevice == AttachmentDevice.World) // Attached to World
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

            if (Highquality)
            {
                if (HighQualityOverlay != this && HighQualityOverlay != null)
                {
                    if (HighQualityOverlay.Highquality)
                    {
                        Debug.LogWarning("Only one Overlay can be in HighQuality mode as per the OpenVR API.");
                        HighQualityOverlay.Highquality = false;
                    }
                    HighQualityOverlay = this;
                }
                else if (HighQualityOverlay == null)
                    HighQualityOverlay = this;

                overlay.SetHighQualityOverlay(_handle);
                overlay.SetOverlayFlag(_handle, VROverlayFlags.Curved, Curved);
                overlay.SetOverlayFlag(_handle, VROverlayFlags.RGSS4X, Antialias);
            }
            else if (overlay.GetHighQualityOverlay() == _handle)
            {
                overlay.SetHighQualityOverlay(OpenVR.k_ulOverlayHandleInvalid);
            }
        }
        else
        {
            overlay.HideOverlay(_handle);
        }
    }

    public void RefreshTexture()
    {
        UpdateTexture(true);
    }

    /// <summary>
    /// Update our texture if we are a RenderTexture.
    /// This is called every frame where nothing else changes, so that we still push RenderTexture updates if needed.
    /// </summary>
    private void UpdateTexture(bool refresh = false)
    {
        if (!(OverlayTexture is RenderTexture && AutoUpdateRenderTextures) && !(OverlayTexture is MovieTexture) && !refresh) return; // This covers the null check for OverlayTexture
        if (_justUpdated) return;
        if (refresh && OverlayTexture == null) return;
        var overlay = OpenVR.Overlay;
        if (overlay == null) return;

        var tex = new Texture_t
        {
            handle = OverlayTexture.GetNativeTexturePtr(),
            eType = SteamVR.instance.graphicsAPI,
            eColorSpace = EColorSpace.Auto
        };

        var vecMouseScale = new HmdVector2_t
        {
            v0 = 1f,
            v1 = (float)OverlayTexture.height / (float)OverlayTexture.width
        };
        overlay.SetOverlayMouseScale(_handle, ref vecMouseScale);

        overlay.SetOverlayTexture(_handle, ref tex);
    }

    #region Structs and Enums

    /// <summary>
    /// Used to determine where an Overlay should be attached.
    /// </summary>
    public enum AttachmentDevice
    {
        /// <summary>
        /// Attempts to attach the Overlay to the World
        /// </summary>
        World,
        /// <summary>
        /// Attempts to attach the Overlay to the Screen / HMD
        /// </summary>
        Screen,
        /// <summary>
        /// Attempts to attach the Overlay to the Left Controller
        /// </summary>
        LeftController,
        /// <summary>
        /// Attempts to attach the Overlay to the Right Controller
        /// </summary>
        RightController,
    }

    /// <summary>
    /// Used when attaching Overlays to Controllers, to determine the base attachment offset.
    /// </summary>
    public enum AttachmentPoint
    {
        /// <summary>
        /// Directly in the center at (0, 0, 0), facing upwards through the Trackpad.
        /// </summary>
        Center,
        /// <summary>
        /// At the end of the controller, like a staff ornament, facing towards the center.
        /// </summary>
        FlatAbove,
        /// <summary>
        /// At the bottom of the controller, facing away from the center.
        /// </summary>
        FlatBelow,
        /// <summary>
        /// At the bottom of the controller, facing towards the center.
        /// </summary>
        FlatBelowFlipped,
        /// <summary>
        /// Just above the Trackpad, facing away from the center.
        /// </summary>
        Above,
        /// <summary>
        /// Just above thr Trackpad, facing the center.
        /// </summary>
        AboveFlipped,
        /// <summary>
        /// Just below the Trigger, facing the center.
        /// </summary>
        Below,
        /// <summary>
        /// Just below the Trigger, facing away from the center.
        /// </summary>
        BelowFlipped,
        /// <summary>
        /// When holding the controller out vertically, Like "Center", but "Up", above the controller.
        /// </summary>
        Up,
        /// <summary>
        /// When holding the controller out vertically, Like "Center", but "Down", below the controller.
        /// </summary>
        Down,
        /// <summary>
        /// When holding the controller out vertically, Like "Center", but "Left", to the side of the controller.
        /// </summary>
        Left,
        /// <summary>
        /// When holding the controller out vertically, Like "Center", but "Right", to the side of the controller.
        /// </summary>
        Right,
    }

    public enum AnimationType
    {
        /// <summary>
        /// Don't animate this Overlay.
        /// </summary>
        None,
        /// <summary>
        /// Animate this Overlay by changing its Alpha.
        /// </summary>
        Alpha,
        /// <summary>
        /// Animate this Overlay by scaling it.
        /// </summary>
        Scale,
        /// <summary>
        /// Animate this Overlay by changing its Alpha and scaling it.
        /// </summary>
        AlphaAndScale,
        DodgeGaze,
    }

    public enum FramerateMode
    {
        _1FPS = 0,
        _2FPS = 1,
        _5FPS = 2,
        _10FPS = 3,
        _15FPS = 4,
        _24FPS = 5,
        _30FPS = 6,
        _60FPS = 7,
        _90FPS = 8,
        _120FPS = 9,
        AsFastAsPossible = 10
    }

    public static readonly List<int> FramerateValues = new List<int>()
    {
        1,
        2,
        5,
        10,
        15,
        24,
        30,
        60,
        90,
        120,
        9999
    };

    #endregion
}