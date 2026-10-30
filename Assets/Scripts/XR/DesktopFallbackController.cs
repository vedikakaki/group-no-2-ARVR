using System;
using System.Collections;
using System.Collections.Generic;
using Interfaces;
using IVRAR.Group02.Environment;
using Resources;
using Targets;
using UnityEngine;
using UnityEngine.XR;

namespace IVRAR.Group02.XR
{
    public enum WeaponType
    {
        Glock17 = 0,
        AssaultRifleM4A1 = 1,
        CombatShotgun = 2
    }

    public enum InputControlMode
    {
        DesktopGameplay = 0,
        XRSimulator = 1
    }

    /// <summary>
    /// Advanced First-Person Desktop & VR Hybrid Controller for the Shooting Range.
    /// Features:
    /// - Solid Physical Collision: Swept-capsule collision preventing player passing through solid objects, hurdles, benches, and walls
    /// - Multi-Weapon Arsenal:
    ///     [1] Glock 17 Tactical Pistol (17 rounds, semi-auto, laser/light, ADS fiber-optic sights)
    ///     [2] M4A1 CQB Assault Rifle (30 rounds, full-auto 630 RPM, holographic sight, foregrip)
    ///     [3] Remington 870 Combat Shotgun (8 shells, pump-action, 8-pellet spread cone)
    /// - Dynamic Weapon Switching: Keys [1], [2], [3] or Mouse Scroll Wheel with smooth draw/holster animations
    /// - Dynamic Recoil, Muzzle Flash, and Spent Brass Ejection for every weapon
    /// - Full Sound Effects: Punchy procedural audio synthesis for all weapons, hits, and reloads
    /// - Interactive Events: Reacts to explosive hazmat barrels, moving tactical enemies, and drill popups
    /// - Esports-grade Tactical HUD with weapon selector dock, ammo bar, combo streaks, and floating combat text
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class DesktopFallbackController : MonoBehaviour
    {
        [Header("Camera & Transforms")]
        [SerializeField] private Camera _targetCamera;
        [SerializeField] private Transform _playerOrigin;

        [Header("Locomotion Tuning")]
        [SerializeField] private float _moveSpeed = 4.0f;
        [SerializeField] private float _sprintSpeed = 6.8f;
        [SerializeField] private float _mouseSensitivity = 2.4f;
        [SerializeField] private bool _invertY = false;

        [Header("Weapon Sights & Offsets")]
        [SerializeField] private float _hipFov = 60f;
        [SerializeField] private float _aimFov = 48f; // ~1.25x iron-sight zoom for Glock 17

        [Header("Audio Clips")]
        [SerializeField] private AudioClip _bgMusicClip;
        [SerializeField] private AudioClip _shotAudioClip;
        [SerializeField] private AudioClip _magOutClip;
        [SerializeField] private AudioClip _magInClip;
        [SerializeField] private AudioClip _slideForwardClip;

        [Header("System Links")]
        [SerializeField] private SimulatedResponseMechanism _responseMechanism;
        [SerializeField] private XRRayInteractorPointer _rayPointer;

        [Header("Mode Configuration")]
        [SerializeField] private bool _autoEnableIfNoHmd = true;
        [SerializeField] private KeyCode _toggleModeKey = KeyCode.F1;
        [SerializeField] private KeyCode _altToggleKey = KeyCode.Tab;

        public static DesktopFallbackController Instance { get; private set; }

        public InputControlMode CurrentControlMode { get; private set; } = InputControlMode.DesktopGameplay;
        public bool IsDesktopModeActive => CurrentControlMode == InputControlMode.DesktopGameplay;

        public bool IsPhysicalQuestHeadset
        {
            get
            {
                if (Application.platform == RuntimePlatform.Android) return true;
                if (!Application.isEditor && XRSettings.isDeviceActive) return true;
                return false;
            }
        }

        public bool UseCameraRelativeWeaponPresentation
        {
            get
            {
                if (IsPhysicalQuestHeadset) return false;
                return true;
            }
        }

        public Camera TargetCamera => _targetCamera;

        public void SetControlMode(InputControlMode mode)
        {
            if (IsPhysicalQuestHeadset) mode = InputControlMode.XRSimulator;
            CurrentControlMode = mode;
            ApplyWeaponParenting();
            ApplyCursorLock(mode == InputControlMode.DesktopGameplay);
            Debug.Log($"[DesktopFallbackController] Switched Input Mode to: {(IsPhysicalQuestHeadset ? "PhysicalXR" : mode.ToString())} (CameraRelative: {UseCameraRelativeWeaponPresentation}, PhysicalQuest: {IsPhysicalQuestHeadset})");
        }

        public void ApplyWeaponParenting()
        {
            if (_viewmodelRoot == null) return;
            _viewmodelRoot.gameObject.SetActive(true);
            Transform parent;
            if (UseCameraRelativeWeaponPresentation)
            {
                parent = _targetCamera != null ? _targetCamera.transform : null;
            }
            else
            {
                EnsureWeaponMount();
                parent = _weaponMount != null ? _weaponMount : _rightControllerAnchor;
            }
            if (parent == null) return;
            if (_viewmodelRoot.parent != parent)
            {
                _viewmodelRoot.SetParent(parent, false);
                Debug.Log($"[QuestVerification] Weapon attached to {parent.name}; physical={IsPhysicalQuestHeadset}");
            }
            _viewmodelRoot.localScale = Vector3.one;
            if (!UseCameraRelativeWeaponPresentation)
            {
                _viewmodelRoot.localPosition = _recoilOffset;
                _viewmodelRoot.localRotation = _recoilRotation;
            }
        }

        public WeaponType CurrentWeapon => _currentWeapon;
        public int CurrentAmmo => _ammoCurrent != null && (int)_currentWeapon < _ammoCurrent.Length ? _ammoCurrent[(int)_currentWeapon] : 0;
        public int MaxAmmo => _ammoCurrent != null && (int)_currentWeapon < AMMO_MAX.Length ? AMMO_MAX[(int)_currentWeapon] : 0;
        public int ShotsFired => _shotsFired;
        public bool IsAiming => _isAiming;
        public bool IsReloading => _isReloading;
        public bool IsFlashlightActive => _flashlightActive;
        public bool IsLaserActive => _laserActive;

        public Transform RightControllerAnchor => _rightControllerAnchor;
        public Transform LeftControllerAnchor => _leftControllerAnchor;
        public Transform WeaponMount => _weaponMount;
        public Transform PlayerOrigin => _playerOrigin;
        public Transform ViewmodelRoot => _viewmodelRoot;

        // XR Controller Device polling & anchors
        [SerializeField] private Transform _rightControllerAnchor;
        [SerializeField] private Transform _leftControllerAnchor;
        [SerializeField] private Transform _weaponMount;
        [Header("Held Weapon Grip")]
        [Tooltip("Model-to-controller rotation. +60 pitch matches this project's Meta XR Simulator resting grip (-60 pitch).")]
        [SerializeField] private Vector3 _weaponGripEuler = new Vector3(60f, 0f, 0f);
        [Tooltip("Additional offset in controller-local metres for grip calibration.")]
        [SerializeField] private Vector3 _weaponGripOffset = Vector3.zero;
        [SerializeField] private XRControllerInputHandler _rightInputHandler;
        [SerializeField] private XRControllerInputHandler _leftInputHandler;
        private InputDevice _rightDevice;
        private InputDevice _leftDevice;
        private readonly List<InputDevice> _xrDeviceBuffer = new List<InputDevice>();
        private bool _prevXrFireState;
        private bool _prevXrReloadState;
        private bool _prevXrSwitchState;
        private bool _prevXrNightState;
        private bool _prevXrAttachmentState;
        private bool _prevXrLaserState;
        private bool _prevXrNvdState;

        [Header("Weapon Obstruction & Collision System")]
        [SerializeField] private bool _enableWeaponObstruction = true;
        private float _currentObstructionOffset = 0f;
        private float _currentObstructionPitch = 0f;
        private bool _isWeaponObstructed = false;
        public bool IsWeaponObstructed => _isWeaponObstructed;
        public float WeaponObstructionOffset => _currentObstructionOffset;

        private Coroutine _switchWeaponCoroutine;

        public void CycleWeapon(int direction, bool instant = false)
        {
            if (_isReloading) return;
            int total = 3;
            int next = ((int)_currentWeapon + direction) % total;
            if (next < 0) next += total;
            SelectWeapon((WeaponType)next, instant);
        }

        public void SelectWeapon(WeaponType newWeapon, bool instant = false)
        {
            if (_isReloading) return;
            if (newWeapon == _currentWeapon && !instant) return;

            if (_switchWeaponCoroutine != null)
            {
                StopCoroutine(_switchWeaponCoroutine);
                _switchWeaponCoroutine = null;
            }

            if (instant)
            {
                _currentWeapon = newWeapon;
                UpdateWeaponModelVisibility();
                _weaponSwitchTimer = 0f;
                if (UseCameraRelativeWeaponPresentation && _viewmodelRoot != null)
                {
                    ApplyWeaponParenting();
                    Vector3 hipPos;
                    switch (_currentWeapon)
                    {
                        case WeaponType.AssaultRifleM4A1: hipPos = new Vector3(0.18f, -0.16f, 0.42f); break;
                        case WeaponType.CombatShotgun: hipPos = new Vector3(0.19f, -0.17f, 0.44f); break;
                        default: hipPos = new Vector3(0.18f, -0.16f, 0.38f); break;
                    }
                    if (!_isAiming)
                    {
                        _viewmodelRoot.localPosition = hipPos;
                        _viewmodelRoot.localRotation = Quaternion.Euler(1.5f, -2.5f, 0f);
                    }
                }
            }
            else
            {
                _switchWeaponCoroutine = StartCoroutine(SwitchWeaponCoroutine(newWeapon));
            }
        }

        public void ToggleFlashlight()
        {
            _flashlightActive = !_flashlightActive;
            if (_tacticalFlashlight != null) _tacticalFlashlight.enabled = _flashlightActive;

            var traineeLight = FindObjectOfType<IVRAR.Group02.Environment.FlashlightController>();
            if (traineeLight != null)
            {
                traineeLight.SetState(_flashlightActive);
            }
        }

        public void ToggleLaser()
        {
            SetLaser(!_laserActive);
        }

        public void SetLaser(bool active)
        {
            _laserActive = active;
            if (_laserLine != null) _laserLine.enabled = _laserActive && _currentWeapon == WeaponType.Glock17;
            if (_laserDot != null) _laserDot.SetActive(_laserActive && _currentWeapon == WeaponType.Glock17);
            if (_laserActive) UpdateLaserSight();
        }

        private float _lastNightToggleTime = -10f;
        private const float NIGHT_TOGGLE_COOLDOWN = 0.25f;

        public void ToggleNightMode()
        {
            ToggleNightMode(false);
        }

        public void ToggleNightMode(bool bypassCooldown)
        {
            if (!bypassCooldown && Time.unscaledTime - _lastNightToggleTime < NIGHT_TOGGLE_COOLDOWN)
            {
                Debug.Log("[DesktopFallbackController] [Night Mode] ToggleNightMode throttled by debounce cooldown.");
                return;
            }
            _lastNightToggleTime = Time.unscaledTime;

            var envMgr = IVRAR.Group02.Environment.LowVisibilityEnvironmentManager.Instance;
            if (envMgr != null)
            {
                envMgr.ToggleNightMode(true);
                Debug.Log($"[DesktopFallbackController] [Night Mode] ToggleNightMode executed via LowVisibilityEnvironmentManager! New isNight = {envMgr.IsNightMode}");
            }
            else
            {
                var envCtrl = FindObjectOfType<IVRAR.Group02.Environment.LowVisibilityEnvironmentController>();
                if (envCtrl != null)
                {
                    envCtrl.ToggleNightMode();
                    Debug.Log("[DesktopFallbackController] [Night Mode] ToggleNightMode executed via LowVisibilityEnvironmentController.");
                }
                else
                {
                    Debug.LogWarning("[DesktopFallbackController] [Night Mode] Cannot toggle: LowVisibilityEnvironmentManager.Instance and LowVisibilityEnvironmentController are both null!");
                }
            }

            var drillMgr = IVRAR.Group02.Environment.TacticalDrillGameManager.Instance ?? FindObjectOfType<IVRAR.Group02.Environment.TacticalDrillGameManager>();
            if (drillMgr != null && envMgr != null)
            {
                drillMgr.SetNightOpsState(envMgr.IsNightMode);
            }
        }

        public void ToggleNightVision()
        {
            var nvd = IVRAR.Group02.Environment.NightVisionSystem.Instance ?? FindObjectOfType<IVRAR.Group02.Environment.NightVisionSystem>();
            if (nvd != null)
            {
                nvd.ToggleNightVision();
            }
        }

        public void TriggerFire()
        {
            if (!_isReloading && _weaponSwitchTimer <= 0f)
            {
                if (_ammoCurrent[(int)_currentWeapon] > 0)
                {
                    ExecuteWeaponShot();
                }
                else
                {
                    StartCoroutine(ReloadCoroutine());
                }
            }
        }

        public void TriggerReload()
        {
            if (!_isReloading && _ammoCurrent[(int)_currentWeapon] < AMMO_MAX[(int)_currentWeapon])
            {
                StartCoroutine(ReloadCoroutine());
            }
        }

        public void CompleteReloadInstant()
        {
            StopAllCoroutines();
            _ammoCurrent[(int)_currentWeapon] = AMMO_MAX[(int)_currentWeapon];
            _isReloading = false;
        }

        public void SetAim(bool aim, bool instant = false)
        {
            _isAiming = aim;
            if (_targetCamera == null) EnsureCameraAndRig();
            if (_targetCamera == null) _targetCamera = Camera.main ?? GetComponent<Camera>();
            if (instant && _targetCamera != null)
            {
                float targetFov;
                Vector3 targetHipPos;
                Vector3 targetAimPos;

                switch (_currentWeapon)
                {
                    case WeaponType.AssaultRifleM4A1:
                        targetFov = aim ? 30f : _hipFov;
                        targetHipPos = new Vector3(0.18f, -0.16f, 0.42f);
                        targetAimPos = new Vector3(0.0f, -0.0699f, 0.35f);
                        break;
                    case WeaponType.CombatShotgun:
                        targetFov = aim ? 52f : _hipFov;
                        targetHipPos = new Vector3(0.19f, -0.17f, 0.44f);
                        targetAimPos = new Vector3(0.0f, -0.0485f, 0.36f);
                        break;
                    default:
                        targetFov = aim ? 48f : _hipFov;
                        targetHipPos = new Vector3(0.18f, -0.16f, 0.38f);
                        targetAimPos = new Vector3(0.0f, -0.0454f, 0.32f);
                        break;
                }
                _targetCamera.fieldOfView = targetFov;

                if (UseCameraRelativeWeaponPresentation && _viewmodelRoot != null)
                {
                    ApplyWeaponParenting();
                    _viewmodelRoot.localPosition = aim ? targetAimPos : targetHipPos;
                    _viewmodelRoot.localRotation = (aim ? Quaternion.identity : Quaternion.Euler(1.5f, -2.5f, 0f));
                }
            }
        }

        // Camera Look
        private float _yaw;
        private float _pitch;
        private bool _cursorLocked = true;

        // Weapon Arsenal State
        private WeaponType _currentWeapon = WeaponType.Glock17;
        private int[] _ammoCurrent = new int[] { 17, 30, 8 };
        private readonly int[] AMMO_MAX = new int[] { 17, 30, 8 };
        private readonly float[] FIRE_RATES = new float[] { 0.16f, 0.095f, 0.72f };
        private readonly bool[] IS_AUTO = new bool[] { false, true, false };

        private bool _isAiming = false;
        private bool _isReloading = false;
        private bool _isObservationOpticActive = false;
        private float _lastShootTime = 0f;
        private float _weaponSwitchTimer = 0f;
        private Vector3 _recoilOffset = Vector3.zero;
        private Quaternion _recoilRotation = Quaternion.identity;
        private bool _xrTriggerFiredEvent = false;

        private void HandleRightTriggerPressed()
        {
            _xrTriggerFiredEvent = true;
        }

        public bool IsObservationOpticActive => _isObservationOpticActive;

        public void SetObservationOptic(bool active)
        {
            _isObservationOpticActive = active;
            Debug.Log($"[DesktopFallbackController] Observation Spotting Optic: {(_isObservationOpticActive ? "ACTIVE (3.5x)" : "INACTIVE")}");
        }

        public void ToggleObservationOptic()
        {
            SetObservationOptic(!_isObservationOpticActive);
        }

        // Viewmodel Root & Weapon Sub-models
        private Transform _viewmodelRoot;
        private GameObject _glockModel;
        private GameObject _rifleModel;
        private GameObject _shotgunModel;
        private Transform _glockSlide;
        private Transform _shotgunPumpSlide;

        // Visual FX
        private Light _muzzleFlashLight;
        private GameObject _muzzleFlashQuad;
        private float _flashTimer = 0f;

        // Tactical Attachments
        private bool _laserActive = true;
        private bool _flashlightActive = false;
        private LineRenderer _laserLine;
        private GameObject _laserDot;
        private Light _tacticalFlashlight;

        // Audio Sources & Procedural Clips
        private AudioSource _musicSource;
        private AudioSource _weaponAudioSource;
        private AudioClip _rifleShotClip;
        private AudioClip _shotgunShotClip;
        private AudioClip _shotgunPumpClip;
        private AudioClip _dryFireClip;

        // Scoring & Combat
        private int _score = 0;
        private int _totalHits = 0;
        private int _headshots = 0;
        private int _shotsFired = 0;
        private int _comboMultiplier = 1;
        private float _comboTimer = 0f;
        private const float COMBO_DURATION = 3.5f;

        // Floating Text & Feedback
        private float _hitMarkerTimer = 0f;
        private string _lastHitText = "";
        private Color _lastHitColor = Color.green;
        private float _hitTextTimer = 0f;
        private float _cameraShakeIntensity = 0f;

        // Weapon Sway & Walking Bobbing
        private float _bobTimer = 0f;
        private bool _showHelpOverlay = true;

        // High-Performance Zero-Allocation Object Pools
        private const int CASING_POOL_SIZE = 32;
        private readonly GameObject[] _casingPool = new GameObject[CASING_POOL_SIZE];
        private readonly Rigidbody[] _casingRbs = new Rigidbody[CASING_POOL_SIZE];
        private readonly Renderer[] _casingRenderers = new Renderer[CASING_POOL_SIZE];
        private int _casingPoolIndex = 0;
        private Material _brassMaterial;
        private Material _shotgunCasingMaterial;

        private const int SPARK_POOL_SIZE = 32;
        private readonly GameObject[] _sparkPool = new GameObject[SPARK_POOL_SIZE];
        private readonly Renderer[] _sparkRenderers = new Renderer[SPARK_POOL_SIZE];
        private int _sparkPoolIndex = 0;
        private Material _sparkEnemyMaterial;
        private Material _sparkTargetMaterial;

        private void InitializeObjectPools()
        {
            Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (litShader != null)
            {
                _brassMaterial = new Material(litShader) { color = new Color(0.85f, 0.70f, 0.25f, 1f) };
                _shotgunCasingMaterial = new Material(litShader) { color = new Color(0.85f, 0.15f, 0.12f, 1f) };
            }

            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            if (unlitShader != null)
            {
                _sparkEnemyMaterial = new Material(unlitShader) { color = Color.red };
                _sparkTargetMaterial = new Material(unlitShader) { color = Color.yellow };
            }

            GameObject poolRoot = new GameObject("FX_ObjectPools");
            poolRoot.transform.SetParent(transform);

            for (int i = 0; i < CASING_POOL_SIZE; i++)
            {
                GameObject casing = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                casing.name = $"PooledCasing_{i}";
                casing.transform.SetParent(poolRoot.transform);
                var col = casing.GetComponent<Collider>();
                if (col != null) Destroy(col);
                casing.SetActive(false);

                _casingPool[i] = casing;
                _casingRbs[i] = casing.AddComponent<Rigidbody>();
                _casingRbs[i].mass = 0.01f;
                _casingRenderers[i] = casing.GetComponent<Renderer>();
            }

            for (int i = 0; i < SPARK_POOL_SIZE; i++)
            {
                GameObject spark = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                spark.name = $"PooledSpark_{i}";
                spark.transform.SetParent(poolRoot.transform);
                var col = spark.GetComponent<Collider>();
                if (col != null) Destroy(col);
                spark.transform.localScale = Vector3.one * 0.07f;
                spark.SetActive(false);

                _sparkPool[i] = spark;
                _sparkRenderers[i] = spark.GetComponent<Renderer>();
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoEnsureController()
        {
            Camera cam = Camera.main ?? FindObjectOfType<Camera>();
            if (cam == null) return;

            if (cam.GetComponent<AudioListener>() == null)
            {
                cam.gameObject.AddComponent<AudioListener>();
            }

            if (FindObjectOfType<DesktopFallbackController>() == null)
            {
                cam.gameObject.AddComponent<DesktopFallbackController>();
                Debug.Log("[DesktopFallbackController] Automatically initialized First-Person Desktop Mode on Camera.");
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                if (Instance._targetCamera == null && _targetCamera != null)
                {
                    Debug.LogWarning($"[DesktopFallbackController] Replacing unconfigured controller on {Instance.gameObject.name} with configured controller on {gameObject.name}.");
                    Destroy(Instance);
                    Instance = this;
                }
                else
                {
                    Debug.LogWarning($"[DesktopFallbackController] Duplicate controller found on {gameObject.name}. Destroying duplicate component.");
                    Destroy(this);
                    return;
                }
            }
            Instance = this;

            EnsureCameraAndRig();
            LoadAudioClips();
            SetupAudioSources();
            InitializeObjectPools();

            IVRAR.Group02.Environment.ExplosiveBarrel.OnBarrelExploded += HandleBarrelExploded;
            IVRAR.Group02.Environment.MovingEnemyController.OnEnemyKilled += HandleEnemyKilled;
        }

        private void OnEnable()
        {
            Application.onBeforeRender += RefreshSimulatorViewPose;
            InputDevices.deviceConnected += HandleDeviceConnected;
            InputDevices.deviceDisconnected += HandleDeviceDisconnected;
        }

        private void OnDisable()
        {
            Application.onBeforeRender -= RefreshSimulatorViewPose;
            InputDevices.deviceConnected -= HandleDeviceConnected;
            InputDevices.deviceDisconnected -= HandleDeviceDisconnected;
            ApplyCursorLock(false);
            if (_musicSource != null) _musicSource.Stop();
            if (_laserLine != null) _laserLine.enabled = false;
            if (_laserDot != null) _laserDot.SetActive(false);
            if (_rightInputHandler != null) _rightInputHandler.OnTriggerPressed -= HandleRightTriggerPressed;
        }

        private void HandleDeviceConnected(InputDevice dev)
        {
            PollXRDevices();
            if (_rightDevice.isValid || _leftDevice.isValid)
            {
                Debug.Log($"[DesktopFallbackController] XR Device connected ({dev.name}). Auto-enabling XRSimulator Mode.");
                SetControlMode(InputControlMode.XRSimulator);
            }
        }

        private void HandleDeviceDisconnected(InputDevice dev)
        {
            PollXRDevices();
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
            IVRAR.Group02.Environment.ExplosiveBarrel.OnBarrelExploded -= HandleBarrelExploded;
            IVRAR.Group02.Environment.MovingEnemyController.OnEnemyKilled -= HandleEnemyKilled;
        }

        private void Start()
        {
            EnsureCameraAndRig();

            if (_targetCamera != null)
            {
                _targetCamera.enabled = true;
                _targetCamera.fieldOfView = _hipFov;
                _yaw = 0f;
                _pitch = 0f;

                if (_targetCamera.GetComponent<AudioListener>() == null)
                {
                    _targetCamera.gameObject.AddComponent<AudioListener>();
                }
            }

            BuildArsenalViewmodels();
            UpdateWeaponModelVisibility();

            // Auto-detect Meta XR Simulator / OpenXR active runtime vs Desktop fallback
            bool isXRActive = XRSettings.isDeviceActive || (UnityEngine.XR.Management.XRGeneralSettings.Instance != null && 
                                                           UnityEngine.XR.Management.XRGeneralSettings.Instance.Manager != null && 
                                                           UnityEngine.XR.Management.XRGeneralSettings.Instance.Manager.activeLoader != null);
            SetControlMode(isXRActive ? InputControlMode.XRSimulator : InputControlMode.DesktopGameplay);
        }

        private void PollXRDevices()
        {
            if (!_rightDevice.isValid)
            {
                _xrDeviceBuffer.Clear();
                // 1. Primary: Search for dedicated Right Controller device (essential for OpenXR & Meta XR Simulator)
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Right | InputDeviceCharacteristics.Controller, _xrDeviceBuffer);
                if (_xrDeviceBuffer.Count > 0 && _xrDeviceBuffer[0].isValid)
                {
                    _rightDevice = _xrDeviceBuffer[0];
                }
                else
                {
                    // 2. Secondary: Search for any device held in right hand
                    _xrDeviceBuffer.Clear();
                    InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Right, _xrDeviceBuffer);
                    if (_xrDeviceBuffer.Count > 0 && _xrDeviceBuffer[0].isValid)
                    {
                        _rightDevice = _xrDeviceBuffer[0];
                    }
                    else
                    {
                        // 3. Fallback: Abstract XRNode
                        _rightDevice = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
                    }
                }

                if (_rightDevice.isValid)
                {
                    Debug.Log($"[DesktopFallbackController] Connected Right XR Controller: {_rightDevice.name} (Characteristics: {_rightDevice.characteristics})");
                }
            }

            if (!_leftDevice.isValid)
            {
                _xrDeviceBuffer.Clear();
                InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller, _xrDeviceBuffer);
                if (_xrDeviceBuffer.Count > 0 && _xrDeviceBuffer[0].isValid)
                {
                    _leftDevice = _xrDeviceBuffer[0];
                }
                else
                {
                    _xrDeviceBuffer.Clear();
                    InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.HeldInHand | InputDeviceCharacteristics.Left, _xrDeviceBuffer);
                    if (_xrDeviceBuffer.Count > 0 && _xrDeviceBuffer[0].isValid)
                    {
                        _leftDevice = _xrDeviceBuffer[0];
                    }
                    else
                    {
                        _leftDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                    }
                }

                if (_leftDevice.isValid)
                {
                    Debug.Log($"[DesktopFallbackController] Connected Left XR Controller: {_leftDevice.name} (Characteristics: {_leftDevice.characteristics})");
                }
            }

            if (_rightControllerAnchor == null)
            {
                var origin = FindObjectOfType<XROriginController>();
                if (origin != null && origin.RightControllerAnchor != null)
                {
                    _rightControllerAnchor = origin.RightControllerAnchor;
                }
            }

            if (_leftControllerAnchor == null)
            {
                var origin = FindObjectOfType<XROriginController>();
                if (origin != null && origin.LeftControllerAnchor != null)
                {
                    _leftControllerAnchor = origin.LeftControllerAnchor;
                }
            }

            if (_rightInputHandler == null && _rightControllerAnchor != null)
            {
                _rightInputHandler = _rightControllerAnchor.GetComponent<XRControllerInputHandler>();
                if (_rightInputHandler == null)
                {
                    _rightInputHandler = _rightControllerAnchor.gameObject.AddComponent<XRControllerInputHandler>();
                }
                _rightInputHandler.Configure(ControllerHand.Right, _rightControllerAnchor);
            }
            if (_rightInputHandler != null)
            {
                _rightInputHandler.OnTriggerPressed -= HandleRightTriggerPressed;
                _rightInputHandler.OnTriggerPressed += HandleRightTriggerPressed;
            }

            if (_leftInputHandler == null && _leftControllerAnchor != null)
            {
                _leftInputHandler = _leftControllerAnchor.GetComponent<XRControllerInputHandler>();
                if (_leftInputHandler == null)
                {
                    _leftInputHandler = _leftControllerAnchor.gameObject.AddComponent<XRControllerInputHandler>();
                }
                _leftInputHandler.Configure(ControllerHand.Left, _leftControllerAnchor);
            }
            if (_leftInputHandler != null)
            {
                _leftInputHandler.OnPrimaryButtonPressed -= ToggleNightMode;
                _leftInputHandler.OnPrimaryButtonPressed += ToggleNightMode;
                _leftInputHandler.OnSecondaryButtonPressed -= ToggleNightMode;
                _leftInputHandler.OnSecondaryButtonPressed += ToggleNightMode;
            }
        }

        public void EnsureCameraAndRig()
        {
            if (_targetCamera == null) _targetCamera = Camera.main;
            if (_targetCamera == null) _targetCamera = FindObjectOfType<Camera>();

            var xrOrigin = FindObjectOfType<XROriginController>();
            if (xrOrigin == null)
            {
                GameObject originGo = new GameObject("XR_Origin_Rig");
                originGo.transform.position = Vector3.zero;
                originGo.transform.rotation = Quaternion.identity;
                xrOrigin = originGo.AddComponent<XROriginController>();
            }

            if (xrOrigin != null)
            {
                _playerOrigin = xrOrigin.transform;
            }
            else if (_targetCamera != null && _targetCamera.transform.parent != null)
            {
                _playerOrigin = _targetCamera.transform.parent;
            }
            else if (_targetCamera != null)
            {
                _playerOrigin = _targetCamera.transform;
            }
            else
            {
                if (_playerOrigin == null) _playerOrigin = transform;
            }

            // Unify XR player rig: Ensure _targetCamera is parented under _playerOrigin (or its CameraOffset)
            // so locomotion and turning move camera and weapons together as ONE rig without drifting or floating
            if (_targetCamera != null && _playerOrigin != null && _playerOrigin != _targetCamera.transform)
            {
                if (!_targetCamera.transform.IsChildOf(_playerOrigin))
                {
                    Transform offset = _playerOrigin.Find("CameraOffset");
                    if (offset == null)
                    {
                        GameObject offsetGo = new GameObject("CameraOffset");
                        offsetGo.transform.SetParent(_playerOrigin, false);
                        offsetGo.transform.localPosition = new Vector3(0f, 1.60f, 0f);
                        offsetGo.transform.localRotation = Quaternion.identity;
                        offset = offsetGo.transform;
                    }
                    _targetCamera.transform.SetParent(offset, true);
                }
            }

            if (_responseMechanism == null) _responseMechanism = FindObjectOfType<SimulatedResponseMechanism>();
            if (_rayPointer == null) _rayPointer = FindObjectOfType<XRRayInteractorPointer>();

            if (xrOrigin != null)
            {
                if (_rightControllerAnchor == null) _rightControllerAnchor = xrOrigin.RightControllerAnchor;
                if (_leftControllerAnchor == null) _leftControllerAnchor = xrOrigin.LeftControllerAnchor;
            }

            Transform parent = _targetCamera != null ? _targetCamera.transform : transform;

            if (_rightControllerAnchor == null)
            {
                Transform existingR = parent.Find("RightControllerAnchor");
                if (existingR != null)
                {
                    _rightControllerAnchor = existingR;
                }
                else
                {
                    GameObject rAnchorObj = new GameObject("RightControllerAnchor");
                    rAnchorObj.transform.SetParent(parent, false);
                    rAnchorObj.transform.localPosition = new Vector3(0.20f, -0.22f, 0.40f);
                    rAnchorObj.transform.localRotation = Quaternion.identity;
                    _rightControllerAnchor = rAnchorObj.transform;
                }
            }

            if (_leftControllerAnchor == null)
            {
                Transform existingL = parent.Find("LeftControllerAnchor");
                if (existingL != null)
                {
                    _leftControllerAnchor = existingL;
                }
                else
                {
                    GameObject lAnchorObj = new GameObject("LeftControllerAnchor");
                    lAnchorObj.transform.SetParent(parent, false);
                    lAnchorObj.transform.localPosition = new Vector3(-0.20f, -0.22f, 0.40f);
                    lAnchorObj.transform.localRotation = Quaternion.identity;
                    _leftControllerAnchor = lAnchorObj.transform;
                }
            }

            if (_rightInputHandler == null && _rightControllerAnchor != null)
            {
                _rightInputHandler = _rightControllerAnchor.GetComponent<XRControllerInputHandler>();
                if (_rightInputHandler == null)
                {
                    _rightInputHandler = _rightControllerAnchor.gameObject.AddComponent<XRControllerInputHandler>();
                }
                _rightInputHandler.Configure(ControllerHand.Right, _rightControllerAnchor);
            }
            if (_rightInputHandler != null)
            {
                _rightInputHandler.OnTriggerPressed -= HandleRightTriggerPressed;
                _rightInputHandler.OnTriggerPressed += HandleRightTriggerPressed;
            }

            if (_leftInputHandler == null && _leftControllerAnchor != null)
            {
                _leftInputHandler = _leftControllerAnchor.GetComponent<XRControllerInputHandler>();
                if (_leftInputHandler == null)
                {
                    _leftInputHandler = _leftControllerAnchor.gameObject.AddComponent<XRControllerInputHandler>();
                }
                _leftInputHandler.Configure(ControllerHand.Left, _leftControllerAnchor);
            }
            if (_leftInputHandler != null)
            {
                _leftInputHandler.OnPrimaryButtonPressed -= ToggleNightMode;
                _leftInputHandler.OnPrimaryButtonPressed += ToggleNightMode;
                _leftInputHandler.OnSecondaryButtonPressed -= ToggleNightMode;
                _leftInputHandler.OnSecondaryButtonPressed += ToggleNightMode;
            }
        }

        public void EnsureWeaponMount()
        {
            if (_rightControllerAnchor == null)
            {
                EnsureCameraAndRig();
            }

            if (_rightControllerAnchor != null && _weaponMount == null)
            {
                Transform existing = _rightControllerAnchor.Find("WeaponMount");
                if (existing != null)
                {
                    _weaponMount = existing;
                }
                else
                {
                    GameObject mountGo = new GameObject("WeaponMount");
                    mountGo.transform.SetParent(_rightControllerAnchor, false);
                    _weaponMount = mountGo.transform;
                }
            }

            if (_weaponMount != null)
            {
                if (IsPhysicalQuestHeadset)
                {
                    // Quest 2 physical Touch controller natural grip alignment:
                    // 12° forward pitch aligns the barrel straight forward along the pointing direction.
                    Vector3 vrGripEuler = new Vector3(12f, 0f, 0f);
                    Vector3 vrGripOffset;
                    switch (_currentWeapon)
                    {
                        case WeaponType.AssaultRifleM4A1:
                            vrGripOffset = new Vector3(0f, -0.01f, 0.08f);
                            break;
                        case WeaponType.CombatShotgun:
                            vrGripOffset = new Vector3(0f, -0.01f, 0.08f);
                            break;
                        default:
                            vrGripOffset = new Vector3(0f, -0.02f, 0.05f);
                            break;
                    }
                    _weaponMount.localPosition = vrGripOffset;
                    _weaponMount.localRotation = Quaternion.Euler(vrGripEuler);
                    _weaponMount.localScale = Vector3.one;
                }
                else
                {
                    _weaponMount.localPosition = Vector3.zero;
                    _weaponMount.localRotation = Quaternion.identity;
                    _weaponMount.localScale = Vector3.one;
                }
            }
        }

        private void LateUpdate()
        {
            if (UseCameraRelativeWeaponPresentation)
            {
                // Synchronize controller anchor with weapon viewmodel for diagnostic consistency
                if (_rightControllerAnchor != null && _viewmodelRoot != null)
                {
                    _rightControllerAnchor.position = _viewmodelRoot.position;
                    _rightControllerAnchor.rotation = _viewmodelRoot.rotation;
                }
            }
        }

        private void RefreshSimulatorViewPose()
        {
            if (UseCameraRelativeWeaponPresentation && _rightControllerAnchor != null && _viewmodelRoot != null)
            {
                _rightControllerAnchor.position = _viewmodelRoot.position;
                _rightControllerAnchor.rotation = _viewmodelRoot.rotation;
            }
        }

        private void UpdateXRAnchorsPose()
        {
            if (CurrentControlMode != InputControlMode.XRSimulator) return;

            Camera cam = _targetCamera != null ? _targetCamera : Camera.main;

            if (IsPhysicalQuestHeadset)
            {
                // Use the same Unity OpenXR reference space as the head camera.
                // OVRInput local poses can use a different tracking origin.
                UpdatePhysicalHand(_rightDevice, _rightControllerAnchor);
                UpdatePhysicalHand(_leftDevice, _leftControllerAnchor);
                if (Time.unscaledTime >= _nextPoseDiagnostic && cam != null)
                {
                    _nextPoseDiagnostic = Time.unscaledTime + 5f;
                    Debug.Log($"[QuestPose] Head={cam.transform.position} Right={_rightControllerAnchor?.position} RightValid={_rightDevice.isValid} HandInHeadSpace={(_rightControllerAnchor != null ? cam.transform.InverseTransformPoint(_rightControllerAnchor.position) : Vector3.zero)}");
                }
                return;
            }

            if (UseCameraRelativeWeaponPresentation)
            {
                if (_rightControllerAnchor != null && _viewmodelRoot != null)
                {
                    _rightControllerAnchor.position = _viewmodelRoot.position;
                    _rightControllerAnchor.rotation = _viewmodelRoot.rotation;
                }
                if (_leftControllerAnchor != null && cam != null)
                {
                    _leftControllerAnchor.position = cam.transform.position + cam.transform.rotation * new Vector3(-0.20f, -0.22f, 0.40f);
                    _leftControllerAnchor.rotation = cam.transform.rotation;
                }
            }
            else
            {
                // Physical Quest Headset: native 6DoF tracking space under CameraOffset
                if (_rightControllerAnchor != null)
                {
                    bool gotR = false;
                    Vector3 rPos = Vector3.zero;
                    Quaternion rRot = Quaternion.identity;

                    try
                    {
                        Vector3 ovrPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
                        Quaternion ovrRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
                        if (ovrPos.sqrMagnitude > 0.0001f || ovrRot != Quaternion.identity)
                        {
                            rPos = ovrPos;
                            rRot = ovrRot;
                            gotR = true;
                        }
                    }
                    catch {}

                    if (!gotR)
                    {
                        Vector3 itPos = InputTracking.GetLocalPosition(XRNode.RightHand);
                        Quaternion itRot = InputTracking.GetLocalRotation(XRNode.RightHand);
                        if (itPos.sqrMagnitude > 0.0001f || itRot != Quaternion.identity)
                        {
                            rPos = itPos;
                            rRot = itRot;
                            gotR = true;
                        }
                    }

                    if (!gotR && _rightDevice.isValid)
                    {
                        bool hasP = _rightDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 dPos);
                        bool hasR = _rightDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion dRot);
                        if (hasP || hasR)
                        {
                            rPos = dPos;
                            rRot = dRot;
                            gotR = true;
                        }
                    }

                    if (gotR)
                    {
                        _rightControllerAnchor.localPosition = rPos;
                        if (rRot != Quaternion.identity) _rightControllerAnchor.localRotation = rRot;
                    }
                }

                if (_leftControllerAnchor != null)
                {
                    bool gotL = false;
                    Vector3 lPos = Vector3.zero;
                    Quaternion lRot = Quaternion.identity;

                    try
                    {
                        Vector3 ovrPos = OVRInput.GetLocalControllerPosition(OVRInput.Controller.LTouch);
                        Quaternion ovrRot = OVRInput.GetLocalControllerRotation(OVRInput.Controller.LTouch);
                        if (ovrPos.sqrMagnitude > 0.0001f || ovrRot != Quaternion.identity)
                        {
                            lPos = ovrPos;
                            lRot = ovrRot;
                            gotL = true;
                        }
                    }
                    catch {}

                    if (!gotL)
                    {
                        Vector3 itPos = InputTracking.GetLocalPosition(XRNode.LeftHand);
                        Quaternion itRot = InputTracking.GetLocalRotation(XRNode.LeftHand);
                        if (itPos.sqrMagnitude > 0.0001f || itRot != Quaternion.identity)
                        {
                            lPos = itPos;
                            lRot = itRot;
                            gotL = true;
                        }
                    }

                    if (!gotL && _leftDevice.isValid)
                    {
                        bool hasP = _leftDevice.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 dPos);
                        bool hasR = _leftDevice.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion dRot);
                        if (hasP || hasR)
                        {
                            lPos = dPos;
                            lRot = dRot;
                            gotL = true;
                        }
                    }

                    if (gotL)
                    {
                        _leftControllerAnchor.localPosition = lPos;
                        if (lRot != Quaternion.identity) _leftControllerAnchor.localRotation = lRot;
                    }
                }
            }
        }

        private float _nextPoseDiagnostic;

        private static void UpdatePhysicalHand(InputDevice device, Transform anchor)
        {
            if (anchor == null || !device.isValid) return;
            if (device.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && !tracked) return;
            if (device.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position))
                anchor.localPosition = position;
            if (device.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rotation))
                anchor.localRotation = rotation;
        }

        private void LoadAudioClips()
        {
            if (_bgMusicClip == null) _bgMusicClip = UnityEngine.Resources.Load<AudioClip>("Audio/Background");
            if (_shotAudioClip == null) _shotAudioClip = UnityEngine.Resources.Load<AudioClip>("Audio/glock_shot");
            if (_magOutClip == null) _magOutClip = UnityEngine.Resources.Load<AudioClip>("Audio/glock_mag_out");
            if (_magInClip == null) _magInClip = UnityEngine.Resources.Load<AudioClip>("Audio/glock_mag_in");
            if (_slideForwardClip == null) _slideForwardClip = UnityEngine.Resources.Load<AudioClip>("Audio/glock_slide_forward");

            // Generate high-impact procedural clips for rifle, shotgun, and pump
            _rifleShotClip = GenerateRifleShotClip();
            _shotgunShotClip = GenerateShotgunShotClip();
            _shotgunPumpClip = GeneratePumpActionClip();
            _dryFireClip = GenerateDryFireClip();
        }

        private void SetupAudioSources()
        {
            if (_musicSource == null)
            {
                GameObject musicObj = new GameObject("Desktop_BGM_Source");
                musicObj.transform.SetParent(transform);
                _musicSource = musicObj.AddComponent<AudioSource>();
                _musicSource.playOnAwake = false;
                _musicSource.loop = true;
                _musicSource.volume = 0.35f;
                _musicSource.spatialBlend = 0f;
            }

            if (_bgMusicClip != null && !_musicSource.isPlaying)
            {
                _musicSource.clip = _bgMusicClip;
                _musicSource.Play();
            }

            if (_weaponAudioSource == null)
            {
                GameObject sfxObj = new GameObject("Desktop_Weapon_SFX");
                sfxObj.transform.SetParent(transform);
                _weaponAudioSource = sfxObj.AddComponent<AudioSource>();
                _weaponAudioSource.playOnAwake = false;
                _weaponAudioSource.loop = false;
                _weaponAudioSource.volume = 1.0f;
                _weaponAudioSource.spatialBlend = 0f;
            }
        }

        #region Weapon Viewmodel Procedural Builders

        public void BuildArsenalViewmodels()
        {
            if (_targetCamera == null) return;
            if (_viewmodelRoot != null) return;

            GameObject root = new GameObject("Tactical_Arsenal_ViewmodelRoot");
            root.transform.SetParent(_targetCamera.transform, false);
            root.transform.localPosition = new Vector3(0.18f, -0.16f, 0.42f);
            root.transform.localRotation = Quaternion.identity;
            _viewmodelRoot = root.transform;

            Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");

            Material blackSteel = new Material(litShader) { color = new Color(0.12f, 0.13f, 0.14f, 1f) };
            Material darkPolymer = new Material(litShader) { color = new Color(0.08f, 0.09f, 0.10f, 1f) };
            Material silverMetal = new Material(litShader) { color = new Color(0.65f, 0.68f, 0.72f, 1f) };
            Material oliveDrab = new Material(litShader) { color = new Color(0.18f, 0.22f, 0.18f, 1f) };
            Material tritiumGreen = new Material(unlitShader) { color = new Color(0.2f, 1.0f, 0.35f, 1f) };
            Material holoRed = new Material(unlitShader) { color = new Color(1.0f, 0.15f, 0.15f, 1f) };

            // 1. GLOCK 17 MODEL
            _glockModel = new GameObject("Glock17_Model");
            _glockModel.transform.SetParent(_viewmodelRoot, false);
            BuildGlockComponents(_glockModel.transform, blackSteel, darkPolymer, silverMetal, tritiumGreen, unlitShader);

            // 2. M4A1 CQB ASSAULT RIFLE MODEL
            _rifleModel = new GameObject("M4A1_Model");
            _rifleModel.transform.SetParent(_viewmodelRoot, false);
            BuildM4A1Components(_rifleModel.transform, blackSteel, darkPolymer, oliveDrab, holoRed);

            // 3. REMINGTON 870 COMBAT SHOTGUN MODEL
            _shotgunModel = new GameObject("Shotgun_Model");
            _shotgunModel.transform.SetParent(_viewmodelRoot, false);
            BuildShotgunComponents(_shotgunModel.transform, blackSteel, darkPolymer, silverMetal, tritiumGreen);

            // 4. Shared Muzzle Flash Light & Quad
            GameObject flashLightObj = new GameObject("MuzzleFlashLight");
            flashLightObj.transform.SetParent(_viewmodelRoot, false);
            flashLightObj.transform.localPosition = new Vector3(0f, 0.035f, 0.35f);
            _muzzleFlashLight = flashLightObj.AddComponent<Light>();
            _muzzleFlashLight.type = LightType.Point;
            _muzzleFlashLight.range = 7f;
            _muzzleFlashLight.intensity = 5.0f;
            _muzzleFlashLight.color = new Color(1.0f, 0.85f, 0.45f);
            _muzzleFlashLight.enabled = false;

            _muzzleFlashQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            _muzzleFlashQuad.name = "MuzzleFlashQuad";
            _muzzleFlashQuad.transform.SetParent(_viewmodelRoot, false);
            _muzzleFlashQuad.transform.localPosition = new Vector3(0f, 0.035f, 0.38f);
            _muzzleFlashQuad.transform.localScale = new Vector3(0.16f, 0.16f, 0.16f);
            Destroy(_muzzleFlashQuad.GetComponent<Collider>());
            _muzzleFlashQuad.GetComponent<Renderer>().material = new Material(unlitShader) { color = new Color(1f, 0.95f, 0.65f, 1f) };
            _muzzleFlashQuad.SetActive(false);

            UpdateWeaponModelVisibility();
        }

        private void BuildGlockComponents(Transform parent, Material blackSteel, Material darkPolymer, Material silverMetal, Material tritiumGreen, Shader unlitShader)
        {
            // Frame
            GameObject frame = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frame.name = "Frame";
            frame.transform.SetParent(parent, false);
            frame.transform.localPosition = new Vector3(0f, 0.005f, 0.05f);
            frame.transform.localScale = new Vector3(0.030f, 0.024f, 0.18f);
            Destroy(frame.GetComponent<Collider>());
            frame.GetComponent<Renderer>().material = darkPolymer;

            // Grip
            GameObject grip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            grip.name = "Grip";
            grip.transform.SetParent(parent, false);
            grip.transform.localPosition = new Vector3(0f, -0.065f, -0.015f);
            grip.transform.localRotation = Quaternion.Euler(18f, 0f, 0f);
            grip.transform.localScale = new Vector3(0.028f, 0.125f, 0.046f);
            Destroy(grip.GetComponent<Collider>());
            grip.GetComponent<Renderer>().material = darkPolymer;

            // Slide
            GameObject slide = GameObject.CreatePrimitive(PrimitiveType.Cube);
            slide.name = "Slide";
            slide.transform.SetParent(parent, false);
            slide.transform.localPosition = new Vector3(0f, 0.028f, 0.05f);
            slide.transform.localScale = new Vector3(0.032f, 0.030f, 0.19f);
            Destroy(slide.GetComponent<Collider>());
            slide.GetComponent<Renderer>().material = blackSteel;
            _glockSlide = slide.transform;

            // Barrel & Sights
            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            barrel.name = "Barrel";
            barrel.transform.SetParent(slide.transform, false);
            barrel.transform.localPosition = new Vector3(0f, -0.05f, 0.52f);
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            barrel.transform.localScale = new Vector3(0.38f, 0.06f, 0.38f);
            Destroy(barrel.GetComponent<Collider>());
            barrel.GetComponent<Renderer>().material = silverMetal;

            // Tritium Front Sight
            GameObject frontSight = GameObject.CreatePrimitive(PrimitiveType.Cube);
            frontSight.name = "FrontSight";
            frontSight.transform.SetParent(slide.transform, false);
            frontSight.transform.localPosition = new Vector3(0f, 0.58f, 0.44f);
            frontSight.transform.localScale = new Vector3(0.12f, 0.22f, 0.08f);
            Destroy(frontSight.GetComponent<Collider>());
            frontSight.GetComponent<Renderer>().material = tritiumGreen;

            // Rear Sight Notch Posts (Left & Right with Tritium Dots)
            GameObject rearSightL = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rearSightL.name = "RearSightLeft";
            rearSightL.transform.SetParent(slide.transform, false);
            rearSightL.transform.localPosition = new Vector3(-0.16f, 0.58f, -0.42f);
            rearSightL.transform.localScale = new Vector3(0.10f, 0.22f, 0.08f);
            Destroy(rearSightL.GetComponent<Collider>());
            rearSightL.GetComponent<Renderer>().material = tritiumGreen;

            GameObject rearSightR = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rearSightR.name = "RearSightRight";
            rearSightR.transform.SetParent(slide.transform, false);
            rearSightR.transform.localPosition = new Vector3(0.16f, 0.58f, -0.42f);
            rearSightR.transform.localScale = new Vector3(0.10f, 0.22f, 0.08f);
            Destroy(rearSightR.GetComponent<Collider>());
            rearSightR.GetComponent<Renderer>().material = tritiumGreen;

            // Tactical Rail & Laser
            GameObject railUnit = GameObject.CreatePrimitive(PrimitiveType.Cube);
            railUnit.name = "LaserLightUnit";
            railUnit.transform.SetParent(parent, false);
            railUnit.transform.localPosition = new Vector3(0f, -0.010f, 0.115f);
            railUnit.transform.localScale = new Vector3(0.026f, 0.022f, 0.065f);
            Destroy(railUnit.GetComponent<Collider>());
            railUnit.GetComponent<Renderer>().material = blackSteel;

            // Flashlight
            GameObject lightObj = new GameObject("TacticalFlashlight");
            lightObj.transform.SetParent(railUnit.transform, false);
            lightObj.transform.localPosition = new Vector3(0f, 0f, 0.6f);
            _tacticalFlashlight = lightObj.AddComponent<Light>();
            _tacticalFlashlight.type = LightType.Spot;
            _tacticalFlashlight.range = 40f;
            _tacticalFlashlight.spotAngle = 36f;
            _tacticalFlashlight.intensity = 2.8f;
            _tacticalFlashlight.color = new Color(0.96f, 0.98f, 1.0f);
            _tacticalFlashlight.enabled = _flashlightActive;

            // Laser Beam & Dot
            _laserLine = railUnit.AddComponent<LineRenderer>();
            _laserLine.useWorldSpace = true;
            _laserLine.startWidth = 0.003f;
            _laserLine.endWidth = 0.003f;
            _laserLine.material = new Material(unlitShader) { color = Color.red };
            _laserLine.startColor = new Color(1f, 0.1f, 0.1f, 0.8f);
            _laserLine.endColor = new Color(1f, 0.1f, 0.1f, 0.8f);
            _laserLine.positionCount = 2;
            _laserLine.enabled = _laserActive;

            _laserDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _laserDot.name = "LaserDot";
            _laserDot.transform.localScale = Vector3.one * 0.035f;
            Destroy(_laserDot.GetComponent<Collider>());
            _laserDot.GetComponent<Renderer>().material = new Material(unlitShader) { color = Color.red };
            _laserDot.SetActive(_laserActive);
        }

        private void BuildM4A1Components(Transform parent, Material blackSteel, Material darkPolymer, Material oliveDrab, Material holoRed)
        {
            // Receiver
            GameObject receiver = GameObject.CreatePrimitive(PrimitiveType.Cube);
            receiver.name = "UpperLowerReceiver";
            receiver.transform.SetParent(parent, false);
            receiver.transform.localPosition = new Vector3(0f, 0.01f, 0.02f);
            receiver.transform.localScale = new Vector3(0.038f, 0.065f, 0.24f);
            Destroy(receiver.GetComponent<Collider>());
            receiver.GetComponent<Renderer>().material = blackSteel;

            // Buffer Tube & Tactical Stock
            GameObject stock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stock.name = "CraneStock";
            stock.transform.SetParent(parent, false);
            stock.transform.localPosition = new Vector3(0f, 0.01f, -0.18f);
            stock.transform.localScale = new Vector3(0.034f, 0.085f, 0.18f);
            Destroy(stock.GetComponent<Collider>());
            stock.GetComponent<Renderer>().material = darkPolymer;

            // Pistol Grip
            GameObject grip = GameObject.CreatePrimitive(PrimitiveType.Cube);
            grip.name = "PistolGrip";
            grip.transform.SetParent(parent, false);
            grip.transform.localPosition = new Vector3(0f, -0.075f, -0.04f);
            grip.transform.localRotation = Quaternion.Euler(22f, 0f, 0f);
            grip.transform.localScale = new Vector3(0.028f, 0.11f, 0.045f);
            Destroy(grip.GetComponent<Collider>());
            grip.GetComponent<Renderer>().material = darkPolymer;

            // Curved 30-round STANAG Magazine
            GameObject mag = GameObject.CreatePrimitive(PrimitiveType.Cube);
            mag.name = "Magazine";
            mag.transform.SetParent(parent, false);
            mag.transform.localPosition = new Vector3(0f, -0.09f, 0.06f);
            mag.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
            mag.transform.localScale = new Vector3(0.026f, 0.15f, 0.055f);
            Destroy(mag.GetComponent<Collider>());
            mag.GetComponent<Renderer>().material = oliveDrab;

            // Handguard Quad-Rail
            GameObject handguard = GameObject.CreatePrimitive(PrimitiveType.Cube);
            handguard.name = "QuadRailHandguard";
            handguard.transform.SetParent(parent, false);
            handguard.transform.localPosition = new Vector3(0f, 0.015f, 0.22f);
            handguard.transform.localScale = new Vector3(0.040f, 0.045f, 0.20f);
            Destroy(handguard.GetComponent<Collider>());
            handguard.GetComponent<Renderer>().material = darkPolymer;

            // Vertical Foregrip
            GameObject vgrip = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            vgrip.name = "VerticalForegrip";
            vgrip.transform.SetParent(parent, false);
            vgrip.transform.localPosition = new Vector3(0f, -0.055f, 0.22f);
            vgrip.transform.localScale = new Vector3(0.025f, 0.045f, 0.025f);
            Destroy(vgrip.GetComponent<Collider>());
            vgrip.GetComponent<Renderer>().material = blackSteel;

            // Barrel & Birdcage Flash Hider
            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            barrel.name = "CarbineBarrel";
            barrel.transform.SetParent(parent, false);
            barrel.transform.localPosition = new Vector3(0f, 0.015f, 0.36f);
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            barrel.transform.localScale = new Vector3(0.018f, 0.12f, 0.018f);
            Destroy(barrel.GetComponent<Collider>());
            barrel.GetComponent<Renderer>().material = blackSteel;

            // EOTech Holographic Optic Sight Housing
            GameObject optic = GameObject.CreatePrimitive(PrimitiveType.Cube);
            optic.name = "HoloSightHousing";
            optic.transform.SetParent(parent, false);
            optic.transform.localPosition = new Vector3(0f, 0.068f, 0.02f);
            optic.transform.localScale = new Vector3(0.034f, 0.038f, 0.09f);
            Destroy(optic.GetComponent<Collider>());
            optic.GetComponent<Renderer>().material = blackSteel;

            // Optical Sight Hood & Clear Window Lens
            GameObject lensFrame = GameObject.CreatePrimitive(PrimitiveType.Cube);
            lensFrame.name = "OpticLensFrame";
            lensFrame.transform.SetParent(optic.transform, false);
            lensFrame.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            lensFrame.transform.localScale = new Vector3(0.92f, 0.88f, 0.95f);
            Destroy(lensFrame.GetComponent<Collider>());
            lensFrame.GetComponent<Renderer>().material = blackSteel;

            // Optical Glass Window
            GameObject glass = GameObject.CreatePrimitive(PrimitiveType.Quad);
            glass.name = "OpticGlass";
            glass.transform.SetParent(lensFrame.transform, false);
            glass.transform.localPosition = new Vector3(0f, 0f, -0.48f);
            glass.transform.localScale = new Vector3(0.78f, 0.78f, 0.78f);
            Destroy(glass.GetComponent<Collider>());
            Shader litShader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (litShader != null)
            {
                Material glassMat = new Material(litShader) { color = new Color(0.12f, 0.22f, 0.28f, 0.35f) };
                glass.GetComponent<Renderer>().material = glassMat;
            }

            // Glowing Holographic Red Circle Reticle
            GameObject reticle = GameObject.CreatePrimitive(PrimitiveType.Quad);
            reticle.name = "HoloReticle";
            reticle.transform.SetParent(optic.transform, false);
            reticle.transform.localPosition = new Vector3(0f, 0.05f, -0.52f);
            reticle.transform.localScale = new Vector3(0.45f, 0.45f, 0.45f);
            Destroy(reticle.GetComponent<Collider>());
            reticle.GetComponent<Renderer>().material = holoRed;
        }

        private void BuildShotgunComponents(Transform parent, Material blackSteel, Material darkPolymer, Material silverMetal, Material tritiumGreen)
        {
            // Receiver
            GameObject receiver = GameObject.CreatePrimitive(PrimitiveType.Cube);
            receiver.name = "ShotgunReceiver";
            receiver.transform.SetParent(parent, false);
            receiver.transform.localPosition = new Vector3(0f, 0.01f, 0.02f);
            receiver.transform.localScale = new Vector3(0.040f, 0.055f, 0.22f);
            Destroy(receiver.GetComponent<Collider>());
            receiver.GetComponent<Renderer>().material = blackSteel;

            // Polymer Stock
            GameObject stock = GameObject.CreatePrimitive(PrimitiveType.Cube);
            stock.name = "SyntheticStock";
            stock.transform.SetParent(parent, false);
            stock.transform.localPosition = new Vector3(0f, -0.02f, -0.19f);
            stock.transform.localRotation = Quaternion.Euler(8f, 0f, 0f);
            stock.transform.localScale = new Vector3(0.034f, 0.085f, 0.22f);
            Destroy(stock.GetComponent<Collider>());
            stock.GetComponent<Renderer>().material = darkPolymer;

            // Main Shotgun Heavy Barrel (12 Gauge)
            GameObject barrel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            barrel.name = "ShotgunBarrel";
            barrel.transform.SetParent(parent, false);
            barrel.transform.localPosition = new Vector3(0f, 0.025f, 0.32f);
            barrel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            barrel.transform.localScale = new Vector3(0.024f, 0.24f, 0.024f);
            Destroy(barrel.GetComponent<Collider>());
            barrel.GetComponent<Renderer>().material = silverMetal;

            // Underbarrel Magazine Tube
            GameObject magTube = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            magTube.name = "MagazineTube";
            magTube.transform.SetParent(parent, false);
            magTube.transform.localPosition = new Vector3(0f, -0.005f, 0.28f);
            magTube.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            magTube.transform.localScale = new Vector3(0.022f, 0.20f, 0.022f);
            Destroy(magTube.GetComponent<Collider>());
            magTube.GetComponent<Renderer>().material = blackSteel;

            // Ribbed Pump Slide Forend
            GameObject pump = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pump.name = "PumpSlide";
            pump.transform.SetParent(parent, false);
            pump.transform.localPosition = new Vector3(0f, -0.005f, 0.24f);
            pump.transform.localScale = new Vector3(0.042f, 0.040f, 0.12f);
            Destroy(pump.GetComponent<Collider>());
            pump.GetComponent<Renderer>().material = darkPolymer;
            _shotgunPumpSlide = pump.transform;

            // High-Visibility Bead Sight
            GameObject bead = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bead.name = "FrontBeadSight";
            bead.transform.SetParent(barrel.transform, false);
            bead.transform.localPosition = new Vector3(0f, 0.98f, 0.6f);
            bead.transform.localScale = new Vector3(0.35f, 0.35f, 0.35f);
            Destroy(bead.GetComponent<Collider>());
            bead.GetComponent<Renderer>().material = tritiumGreen;

            // Ventilated Top Sight Rib
            GameObject rib = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rib.name = "VentilatedSightRib";
            rib.transform.SetParent(barrel.transform, false);
            rib.transform.localPosition = new Vector3(0f, 0.52f, 0f);
            rib.transform.localScale = new Vector3(0.20f, 0.96f, 0.08f);
            Destroy(rib.GetComponent<Collider>());
            rib.GetComponent<Renderer>().material = blackSteel;
        }

        private void UpdateWeaponModelVisibility()
        {
            if (_glockModel != null) _glockModel.SetActive(_currentWeapon == WeaponType.Glock17);
            if (_rifleModel != null) _rifleModel.SetActive(_currentWeapon == WeaponType.AssaultRifleM4A1);
            if (_shotgunModel != null) _shotgunModel.SetActive(_currentWeapon == WeaponType.CombatShotgun);

            if (_laserLine != null) _laserLine.enabled = _laserActive && _currentWeapon == WeaponType.Glock17;
            if (_laserDot != null) _laserDot.SetActive(_laserActive && _currentWeapon == WeaponType.Glock17);
        }

        #endregion

        #region Input & Updates

        private void Update()
        {
            if (_weaponSwitchTimer > 0f)
            {
                _weaponSwitchTimer -= Time.deltaTime;
                if (_weaponSwitchTimer < 0f) _weaponSwitchTimer = 0f;
            }

            PollXRDevices();
            UpdateXRAnchorsPose();

            // Mode switching hotkeys: F1 (toggle), F2 (XR Simulator), F3 (Desktop Gameplay)
            if (Input.GetKeyDown(KeyCode.F1))
            {
                SetControlMode(CurrentControlMode == InputControlMode.DesktopGameplay 
                    ? InputControlMode.XRSimulator 
                    : InputControlMode.DesktopGameplay);
            }
            else if (Input.GetKeyDown(KeyCode.F2))
            {
                SetControlMode(InputControlMode.XRSimulator);
            }
            else if (Input.GetKeyDown(KeyCode.F3))
            {
                SetControlMode(InputControlMode.DesktopGameplay);
            }

            // Global Day/Night Mode hotkeys: N, F4, or ~ (Tilde/BackQuote) - always available in ANY mode
            if (Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.F4) || Input.GetKeyDown(KeyCode.BackQuote))
            {
                ToggleNightMode();
            }

            if (Input.GetKeyDown(KeyCode.H))
            {
                _showHelpOverlay = !_showHelpOverlay;
            }

            if (CurrentControlMode == InputControlMode.DesktopGameplay)
            {
                HandleCursorLock();
                HandleWeaponSwitch();
                HandleMouseLook();
                HandleLocomotion();
                HandleAimingAndZoom();
                HandleShootingAndReload();
                HandleTacticalAttachments();
            }
            else
            {
                HandleXRSimulatorControls();
            }

            UpdateWeaponSwayAndRecoil();
            UpdateLaserSight();
            UpdateCombosAndEffects();
        }

        private bool IsOvrTriggerHeld()
        {
            try
            {
                return OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) ||
                       OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger, OVRInput.Controller.RTouch) >= 0.20f ||
                       OVRInput.Get(OVRInput.RawButton.RIndexTrigger) ||
                       OVRInput.Get(OVRInput.RawAxis1D.RIndexTrigger) >= 0.20f ||
                       OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger) ||
                       OVRInput.Get(OVRInput.Axis1D.PrimaryIndexTrigger) >= 0.20f;
            }
            catch { return false; }
        }

        private bool IsOvrTriggerDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) ||
                       OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) ||
                       OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger);
            }
            catch { return false; }
        }

        private bool IsOvrGripHeld()
        {
            try
            {
                return OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch) ||
                       OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.RTouch) >= 0.25f ||
                       OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.LTouch) ||
                       OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, OVRInput.Controller.LTouch) >= 0.25f;
            }
            catch { return false; }
        }

        private bool IsOvrReloadDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch);
            }
            catch { return false; }
        }

        private bool IsOvrSwitchDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch) ||
                       OVRInput.GetDown(OVRInput.RawButton.RThumbstick);
            }
            catch { return false; }
        }

        private bool IsOvrFlashlightDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch) ||
                       OVRInput.GetDown(OVRInput.RawButton.B);
            }
            catch { return false; }
        }

        private bool IsOvrNightDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch) ||
                       OVRInput.GetDown(OVRInput.RawButton.X) ||
                       OVRInput.GetDown(OVRInput.Button.Three) ||
                       OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) ||
                       OVRInput.GetDown(OVRInput.RawButton.Y) ||
                       OVRInput.GetDown(OVRInput.Button.Four);
            }
            catch { return false; }
        }

        private bool IsOvrLaserDown()
        {
            try
            {
                return OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch);
            }
            catch { return false; }
        }

        private void HandleXRSimulatorControls()
        {
            try { OVRInput.Update(); } catch {}

            // 1. XR Locomotion (Left Stick 2D Axis from XR handler, OVRInput, or InputDevice)
            Vector2 lStick = Vector2.zero;
            if (_leftInputHandler != null && _leftInputHandler.ThumbstickAxis.sqrMagnitude > 0.01f)
            {
                lStick = _leftInputHandler.ThumbstickAxis;
            }
            else if (_leftDevice.isValid && _leftDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 devLStick) && devLStick.sqrMagnitude > 0.01f)
            {
                lStick = devLStick;
            }
            else
            {
                try
                {
                    Vector2 ovrLStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);
                    if (ovrLStick.sqrMagnitude > 0.01f) lStick = ovrLStick;
                }
                catch {}
            }

            // Keyboard WASD fallback when stick is not actively deflected:
            if (lStick.sqrMagnitude < 0.01f)
            {
                float h = Input.GetAxisRaw("Horizontal");
                float v = Input.GetAxisRaw("Vertical");
                if (Mathf.Abs(h) > 0.05f || Mathf.Abs(v) > 0.05f)
                {
                    lStick = new Vector2(h, v).normalized;
                }
            }

            if (Mathf.Abs(lStick.x) > 0.15f || Mathf.Abs(lStick.y) > 0.15f)
            {
                PerformLocomotion(lStick, _moveSpeed);
            }

            // 2. XR Turn (Right Stick X from XR handler, OVRInput, or InputDevice)
            float rStickX = 0f;
            if (_rightInputHandler != null && Mathf.Abs(_rightInputHandler.ThumbstickAxis.x) > 0.15f)
            {
                rStickX = _rightInputHandler.ThumbstickAxis.x;
            }
            else if (_rightDevice.isValid && _rightDevice.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 devRStick) && Mathf.Abs(devRStick.x) > 0.15f)
            {
                rStickX = devRStick.x;
            }
            else
            {
                try
                {
                    Vector2 ovrRStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
                    if (Mathf.Abs(ovrRStick.x) > 0.15f) rStickX = ovrRStick.x;
                }
                catch {}
            }

            if (Mathf.Abs(rStickX) > 0.2f && _playerOrigin != null)
            {
                PerformTurn(rStickX * 90f * Time.deltaTime);
            }
            else if (Input.GetMouseButton(1) && _playerOrigin != null)
            {
                // Fallback mouse look in simulator mode
                float mouseX = Input.GetAxis("Mouse X") * _mouseSensitivity;
                PerformTurn(mouseX);
            }

            // 3. XR Aim (Right or Left Grip - supports float analog + bool button)
            bool xrGripAim = (_rightInputHandler != null && _rightInputHandler.IsGripPressed) ||
                             (_leftInputHandler != null && _leftInputHandler.IsGripPressed) ||
                             IsOvrGripHeld();

            if (!xrGripAim && _rightDevice.isValid)
            {
                if (_rightDevice.TryGetFeatureValue(CommonUsages.grip, out float rGripVal) && rGripVal >= 0.25f) xrGripAim = true;
                if (_rightDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool rGripBtn) && rGripBtn) xrGripAim = true;
            }
            if (!xrGripAim && _leftDevice.isValid)
            {
                if (_leftDevice.TryGetFeatureValue(CommonUsages.grip, out float lGripVal) && lGripVal >= 0.25f) xrGripAim = true;
                if (_leftDevice.TryGetFeatureValue(CommonUsages.gripButton, out bool lGripBtn) && lGripBtn) xrGripAim = true;
            }
            // Hybrid fallback: Left Mouse Button hold matches desktop ADS aim; keyboard Z/G
            if (!xrGripAim && (Input.GetMouseButton(0) || Input.GetKey(KeyCode.Z) || Input.GetKey(KeyCode.G))) xrGripAim = true;
            _isAiming = xrGripAim;

            // 4. XR Reload (Right Controller Primary Button 'A' avoids simulator's R key elevation conflict)
            bool xrReloadDown = (_rightInputHandler != null && _rightInputHandler.IsPrimaryPressed && !_prevXrReloadState) ||
                                IsOvrReloadDown();

            if (!xrReloadDown && _rightDevice.isValid && _rightDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool aBtn) && aBtn)
            {
                if (!_prevXrReloadState)
                {
                    xrReloadDown = true;
                }
                _prevXrReloadState = true;
            }
            else if (_rightInputHandler != null && _rightInputHandler.IsPrimaryPressed)
            {
                _prevXrReloadState = true;
            }
            else
            {
                _prevXrReloadState = false;
            }

            // Hybrid fallback: keyboard R
            if (Input.GetKeyDown(KeyCode.R)) xrReloadDown = true;

            int widx = (int)_currentWeapon;
            if (xrReloadDown && !_isReloading && _ammoCurrent[widx] < AMMO_MAX[widx])
            {
                Debug.Log(string.Format("[DesktopFallbackController] [XR Input] Right Controller Primary Button 'A' (Reload) pressed for {0} (Ammo: {1}/{2}).",
                    _currentWeapon, _ammoCurrent[widx], AMMO_MAX[widx]));
                StartCoroutine(ReloadCoroutine());
                return;
            }

            // 5. XR Fire (Right Controller Trigger)
            string triggerSource = null;
            bool xrTriggerHeld = false;

            // A. Primary: Hardware/Simulated XR Controller Device (OpenXR / Meta XR Simulator)
            if (_rightDevice.isValid)
            {
                if (_rightDevice.TryGetFeatureValue(CommonUsages.trigger, out float trigVal) && trigVal >= 0.20f)
                {
                    xrTriggerHeld = true;
                    triggerSource = $"XR Device Analog Trigger ({trigVal:F2})";
                }
                else if (_rightDevice.TryGetFeatureValue(CommonUsages.triggerButton, out bool trigBtn) && trigBtn)
                {
                    xrTriggerHeld = true;
                    triggerSource = "XR Device Trigger Button";
                }
            }

            // B. Meta XR SDK / Simulator (OVRInput polling)
            if (!xrTriggerHeld && IsOvrTriggerHeld())
            {
                xrTriggerHeld = true;
                triggerSource = "Meta XR OVRInput Trigger";
            }

            // C. Dedicated XRControllerInputHandler component
            if (!xrTriggerHeld && _rightInputHandler != null && _rightInputHandler.IsTriggerPressed)
            {
                xrTriggerHeld = true;
                triggerSource = $"XRControllerInputHandler ({_rightInputHandler.TriggerValue:F2})";
            }

            // D. Virtual axis / button fallback
            if (!xrTriggerHeld)
            {
                try
                {
                    if (Input.GetButton("Fire1"))
                    {
                        xrTriggerHeld = true;
                        triggerSource = "Input.Fire1 Button";
                    }
                }
                catch {}
            }

            // XR Trigger Edge Detection
            bool ovrTriggerDown = IsOvrTriggerDown();
            bool xrTriggerPress = (xrTriggerHeld && !_prevXrFireState) || _xrTriggerFiredEvent || ovrTriggerDown;
            _xrTriggerFiredEvent = false;
            _prevXrFireState = xrTriggerHeld;

            // Hybrid Mouse & Keyboard fallbacks (Right Mouse Button matches desktop Fire; Space/Return/Ctrl fallback)
            bool mouseFireDown = Input.GetMouseButtonDown(1) || 
                                 (!_isAiming && Input.GetMouseButtonDown(0)) || 
                                 Input.GetKeyDown(KeyCode.Space) || 
                                 Input.GetKeyDown(KeyCode.Return) || 
                                 Input.GetKeyDown(KeyCode.LeftControl);

            bool mouseFireHeld = Input.GetMouseButton(1) || 
                                 (!_isAiming && Input.GetMouseButton(0)) || 
                                 Input.GetKey(KeyCode.Space) || 
                                 Input.GetKey(KeyCode.Return) || 
                                 Input.GetKey(KeyCode.LeftControl);

            if ((mouseFireDown || mouseFireHeld) && triggerSource == null)
            {
                triggerSource = Input.GetMouseButtonDown(1) ? "Desktop Mouse RMB" : 
                                (Input.GetKeyDown(KeyCode.Space) ? "Keyboard Spacebar" : "Desktop Mouse/Key");
            }

            bool fireInput = IS_AUTO[widx] ? (xrTriggerHeld || mouseFireHeld) : (xrTriggerPress || mouseFireDown);
            bool rawTriggerDetected = xrTriggerHeld || xrTriggerPress || mouseFireHeld || mouseFireDown;

            // Diagnostic Log: Distinguish Trigger Detected vs Not Detected
            if (rawTriggerDetected)
            {
                Debug.Log(string.Format("[XR Firing Diagnostic] Trigger DETECTED via {0} | Weapon: {1} | Ammo: {2}/{3} | Auto: {4} | Press: {5} | Held: {6}",
                    triggerSource ?? "Unknown",
                    _currentWeapon,
                    _ammoCurrent[widx],
                    AMMO_MAX[widx],
                    IS_AUTO[widx],
                    (xrTriggerPress || mouseFireDown),
                    (xrTriggerHeld || mouseFireHeld)));

                if (!fireInput && (xrTriggerHeld || mouseFireHeld))
                {
                    Debug.Log(string.Format("[XR Firing Diagnostic] Trigger detected ({0}) but semi-auto firing callback waiting for release before next shot.",
                        triggerSource));
                }
            }
            else if (Time.frameCount % 180 == 0)
            {
                // Throttled heartbeat: Trigger not detected
                Debug.Log(string.Format("[XR Firing Diagnostic] Idle: Trigger NOT detected | Mode: {0} | RightDevice: {1} (valid={2}) | OVR Connected={3}",
                    CurrentControlMode,
                    _rightDevice.name,
                    _rightDevice.isValid,
                    (_rightInputHandler != null && _rightInputHandler.IsConnected)));
            }

            if (fireInput)
            {
                if (_isReloading)
                {
                    Debug.LogWarning(string.Format("[XR Firing Diagnostic] Firing callback called but weapon action BLOCKED: Weapon {0} is RELOADING ({1}/{2}).",
                        _currentWeapon, _ammoCurrent[widx], AMMO_MAX[widx]));
                }
                else if (_weaponSwitchTimer > 0f)
                {
                    Debug.LogWarning(string.Format("[XR Firing Diagnostic] Firing callback called but weapon action BLOCKED: Weapon switch cooldown active ({0:F2}s remaining).",
                        _weaponSwitchTimer));
                }
                else if (_ammoCurrent[widx] <= 0)
                {
                    Debug.LogWarning(string.Format("[XR Firing Diagnostic] Firing callback called but weapon action BLOCKED: Ammo empty (0/{0}). Triggering dry-fire & auto-reload.",
                        AMMO_MAX[widx]));

                    if ((xrTriggerPress || mouseFireDown) && _dryFireClip != null && _weaponAudioSource != null)
                    {
                        _weaponAudioSource.PlayOneShot(_dryFireClip, 0.7f);
                    }
                    StartCoroutine(ReloadCoroutine());
                    return;
                }
                else if (Time.time - _lastShootTime < FIRE_RATES[widx])
                {
                    Debug.Log(string.Format("[XR Firing Diagnostic] Firing callback called but weapon action BLOCKED: Rate-limited ({0:F3}s < {1:F3}s).",
                        Time.time - _lastShootTime, FIRE_RATES[widx]));
                }
                else
                {
                    ExecuteWeaponShot();
                }
            }

            // 6. XR Weapon Switching: Right Stick Click OR mouse scroll / numbers
            bool currSwitchRaw = (_rightInputHandler != null && _rightInputHandler.IsThumbstickClicked) ||
                                 (_rightDevice.isValid && _rightDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool rStickClick) && rStickClick);
            bool xrSwitchDown = (currSwitchRaw && !_prevXrSwitchState) || IsOvrSwitchDown();
            _prevXrSwitchState = currSwitchRaw;

            if (xrSwitchDown)
            {
                Debug.Log("[DesktopFallbackController] [XR Input] Weapon Switch Triggered via Right Stick Click.");
            }

            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (xrSwitchDown || scroll > 0.02f)
            {
                if (!_isReloading) CycleWeapon(1);
            }
            else if (scroll < -0.02f)
            {
                if (!_isReloading) CycleWeapon(-1);
            }
            if (Input.GetKeyDown(KeyCode.Alpha1)) SelectWeapon(WeaponType.Glock17);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SelectWeapon(WeaponType.AssaultRifleM4A1);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SelectWeapon(WeaponType.CombatShotgun);

            // 7. XR Flashlight: Right Controller Secondary Button (B) OR key F
            bool currFlashlightRaw = (_rightInputHandler != null && _rightInputHandler.IsSecondaryPressed) ||
                                     (_rightDevice.isValid && _rightDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool bBtn) && bBtn);
            bool xrFlashlightDown = (currFlashlightRaw && !_prevXrAttachmentState) || IsOvrFlashlightDown();
            _prevXrAttachmentState = currFlashlightRaw;

            if (xrFlashlightDown || Input.GetKeyDown(KeyCode.F))
            {
                ToggleFlashlight();
                Debug.Log("[DesktopFallbackController] [XR Input] Right Controller Secondary Button 'B' / Key F (Flashlight) pressed.");
            }

            // 8. XR Night Mode: Left Controller Button X (Primary) OR Button Y (Secondary) on Quest / XR Simulator
            bool currNightRaw = (_leftInputHandler != null && (_leftInputHandler.IsPrimaryPressed || _leftInputHandler.IsSecondaryPressed)) ||
                                (_leftDevice.isValid && _leftDevice.TryGetFeatureValue(CommonUsages.primaryButton, out bool xBtn) && xBtn) ||
                                (_leftDevice.isValid && _leftDevice.TryGetFeatureValue(CommonUsages.secondaryButton, out bool yBtn) && yBtn);
            bool xrNightDown = (currNightRaw && !_prevXrNightState) || IsOvrNightDown();
            _prevXrNightState = currNightRaw;

            if (xrNightDown || Input.GetKeyDown(KeyCode.N) || Input.GetKeyDown(KeyCode.F4) || Input.GetKeyDown(KeyCode.BackQuote))
            {
                Debug.Log($"[DesktopFallbackController] [XR Input] Left Controller Button (X / Y / Simulated) pressed (raw={currNightRaw}, ovr={IsOvrNightDown()}) -> Toggling Night Mode.");
                ToggleNightMode();
            }

            // 9. XR Tactical Laser: Left Stick Click OR key T / L
            bool currLaserRaw = (_leftInputHandler != null && _leftInputHandler.IsThumbstickClicked) ||
                                (_leftDevice.isValid && _leftDevice.TryGetFeatureValue(CommonUsages.primary2DAxisClick, out bool lStickClick) && lStickClick);
            bool xrLaserDown = (currLaserRaw && !_prevXrLaserState) || IsOvrLaserDown();
            _prevXrLaserState = currLaserRaw;

            if (xrLaserDown || Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.L))
            {
                ToggleLaser();
                Debug.Log("[DesktopFallbackController] [XR Input] Tactical Laser Toggled via Left Controller / Key T/L.");
            }

            // 10. XR Night Vision Device (NVD): Key V
            if (Input.GetKeyDown(KeyCode.V))
            {
                ToggleNightVision();
                Debug.Log("[DesktopFallbackController] [XR Input] Night Vision Toggled (Key V).");
            }

            // 11. Observation Spotter Optic: Key O
            if (Input.GetKeyDown(KeyCode.O)) ToggleObservationOptic();
        }

        private void HandleWeaponSwitch()
        {
            if (_isReloading) return;

            // 1. Desktop Number Hotkeys: 1 = Glock 17, 2 = M4A1, 3 = Shotgun
            if (Input.GetKeyDown(KeyCode.Alpha1)) SelectWeapon(WeaponType.Glock17);
            else if (Input.GetKeyDown(KeyCode.Alpha2)) SelectWeapon(WeaponType.AssaultRifleM4A1);
            else if (Input.GetKeyDown(KeyCode.Alpha3)) SelectWeapon(WeaponType.CombatShotgun);

            // 2. Mouse Scroll Wheel: Scroll Up -> Next Weapon, Scroll Down -> Previous Weapon
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (scroll > 0.02f)
            {
                CycleWeapon(1);
            }
            else if (scroll < -0.02f)
            {
                CycleWeapon(-1);
            }
        }

        private IEnumerator SwitchWeaponCoroutine(WeaponType newWeapon)
        {
            _weaponSwitchTimer = 0.22f;

            // Lower weapon
            Vector3 origPos = _viewmodelRoot != null ? _viewmodelRoot.localPosition : Vector3.zero;
            Vector3 lowerPos = origPos + Vector3.down * 0.22f;

            float t = 0f;
            while (t < 0.10f)
            {
                if (_viewmodelRoot != null) _viewmodelRoot.localPosition = Vector3.Lerp(origPos, lowerPos, t / 0.10f);
                t += Time.deltaTime;
                yield return null;
            }

            _currentWeapon = newWeapon;
            UpdateWeaponModelVisibility();

            // Play equip click
            if (_slideForwardClip != null && _weaponAudioSource != null)
            {
                _weaponAudioSource.PlayOneShot(_slideForwardClip, 0.75f);
            }

            // Raise new weapon
            t = 0f;
            while (t < 0.12f)
            {
                if (_viewmodelRoot != null) _viewmodelRoot.localPosition = Vector3.Lerp(lowerPos, origPos, t / 0.12f);
                t += Time.deltaTime;
                yield return null;
            }

            _weaponSwitchTimer = 0f;
            _switchWeaponCoroutine = null;
        }

        private void HandleCursorLock()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                ApplyCursorLock(false);
            }
            else if ((Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1)) && !_cursorLocked)
            {
                ApplyCursorLock(true);
            }
        }

        public void ApplyCursorLock(bool locked)
        {
            _cursorLocked = locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void HandleMouseLook()
        {
            if (_targetCamera == null) return;

            if (_cursorLocked || Application.isEditor)
            {
                float currentSens = _isAiming ? _mouseSensitivity * 0.45f : _mouseSensitivity;
                float mouseX = Input.GetAxis("Mouse X") * currentSens;
                float mouseY = Input.GetAxis("Mouse Y") * currentSens * (_invertY ? 1f : -1f);

                _yaw += mouseX;
                _pitch = Mathf.Clamp(_pitch - mouseY, -80f, 80f);
            }

            // Apply horizontal yaw to body origin
            if (_playerOrigin != null && _playerOrigin != _targetCamera.transform)
            {
                _playerOrigin.rotation = Quaternion.Euler(0f, _yaw, 0f);
            }

            // Apply pitch directly to camera in Desktop mode
            _targetCamera.transform.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        /// <summary>
        /// Solid Locomotion with Swept-Capsule Collision Detection.
        /// Prevents passing through solid objects, hurdles, benches, and walls.
        /// Slides smoothly along obstacle surfaces.
        /// </summary>
        private void HandleLocomotion()
        {
            if (_playerOrigin == null || _targetCamera == null) return;

            float horizontal = Input.GetAxis("Horizontal"); // A/D
            float vertical = Input.GetAxis("Vertical");     // W/S

            if (Mathf.Abs(horizontal) > 0.02f || Mathf.Abs(vertical) > 0.02f)
            {
                float speed = Input.GetKey(KeyCode.LeftShift) ? _sprintSpeed : _moveSpeed;
                PerformLocomotion(new Vector2(horizontal, vertical).normalized, speed);
            }
            else
            {
                _bobTimer = Mathf.MoveTowards(_bobTimer, 0f, Time.deltaTime * 3.5f);
            }
        }

        public void PerformLocomotion(Vector2 input, float speed)
        {
            if (_playerOrigin == null || _targetCamera == null) return;
            if (input.sqrMagnitude < 0.001f) return;

            Vector3 forward = _targetCamera.transform.forward;
            forward.y = 0f;
            forward.Normalize();

            Vector3 right = _targetCamera.transform.right;
            right.y = 0f;
            right.Normalize();

            Vector3 moveDir = (forward * input.y + right * input.x).normalized;
            float dt = Time.unscaledDeltaTime > 0.0001f ? Time.unscaledDeltaTime : 0.0166f;
            float moveDist = speed * dt;

            // Swept Capsule Collision against solid environment
            Vector3 currentPos = _playerOrigin.position;
            Vector3 bottom = currentPos + Vector3.up * 0.40f;
            Vector3 top = currentPos + Vector3.up * 1.50f;
            float radius = 0.32f;
            int layerMask = ~0;

            if (Physics.CapsuleCast(bottom, top, radius, moveDir, out RaycastHit hit, moveDist + 0.04f, layerMask, QueryTriggerInteraction.Ignore))
            {
                // Blocked by solid object: Move up to contact and slide along normal
                float allowedDist = Mathf.Max(0f, hit.distance - 0.04f);
                currentPos += moveDir * allowedDist;

                Vector3 slideDir = Vector3.ProjectOnPlane(moveDir, hit.normal).normalized;
                float remDist = moveDist - allowedDist;
                if (slideDir.sqrMagnitude > 0.001f && remDist > 0.001f)
                {
                    if (!Physics.CapsuleCast(currentPos + Vector3.up * 0.40f, currentPos + Vector3.up * 1.50f, radius, slideDir, out RaycastHit slideHit, remDist + 0.04f, layerMask, QueryTriggerInteraction.Ignore))
                    {
                        currentPos += slideDir * remDist;
                    }
                    else
                    {
                        currentPos += slideDir * Mathf.Max(0f, slideHit.distance - 0.04f);
                    }
                }
            }
            else
            {
                currentPos += moveDir * moveDist;
            }

            // Range Bounds Clamping
            currentPos.x = Mathf.Clamp(currentPos.x, -14.2f, 14.2f);
            currentPos.z = Mathf.Clamp(currentPos.z, -3.5f, 31.0f);

            // Keep Y steady: floor level (0) if origin is the rig, or eye height (1.65) if origin is the camera directly
            bool isFloorRig = (_playerOrigin != _targetCamera.transform);
            currentPos.y = isFloorRig ? 0f : 1.65f;

            _playerOrigin.position = currentPos;
            _bobTimer += Time.deltaTime * (speed * 1.8f);
        }

        public void PerformTurn(float yawAngle)
        {
            if (_playerOrigin == null || Mathf.Abs(yawAngle) < 0.001f) return;

            if (_targetCamera != null && _playerOrigin != _targetCamera.transform)
            {
                // Rotate rig around camera ground position so head does not swing in an arc
                Vector3 cameraGroundPos = new Vector3(_targetCamera.transform.position.x, _playerOrigin.position.y, _targetCamera.transform.position.z);
                _playerOrigin.RotateAround(cameraGroundPos, Vector3.up, yawAngle);
            }
            else
            {
                _playerOrigin.Rotate(0f, yawAngle, 0f);
            }
        }

        private void HandleAimingAndZoom()
        {
            if (_targetCamera == null) return;

            // In VR/Simulator mode, preserve headset FOV and VR optical sight alignment without artificial FOV distortion
            if (CurrentControlMode == InputControlMode.XRSimulator)
            {
                if (Mathf.Abs(_targetCamera.fieldOfView - _hipFov) > 0.05f)
                    _targetCamera.fieldOfView = Mathf.Lerp(_targetCamera.fieldOfView, _hipFov, Time.deltaTime * 14f);
                return;
            }

            float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : 0.033f;

            // Observation Spotting Optic takes priority for distant marker inspection
            if (_isObservationOpticActive)
            {
                _targetCamera.fieldOfView = Mathf.Lerp(_targetCamera.fieldOfView, 20f, dt * 14f); // ~3.5x magnification for distant observation
                return;
            }

            // Desktop mode: Left Mouse Button holds Aim Down Sights (ADS)
            if (Input.GetMouseButtonDown(0)) _isAiming = true;
            else if (Input.GetMouseButtonUp(0)) _isAiming = false;

            // Smooth FOV transition on desktop
            float targetFov;
            switch (_currentWeapon)
            {
                case WeaponType.AssaultRifleM4A1: 
                    targetFov = _isAiming ? 30f : _hipFov; // ~2.0x optical scope zoom
                    break;
                case WeaponType.CombatShotgun: 
                    targetFov = _isAiming ? 52f : _hipFov; // ~1.15x mild iron-sight zoom
                    break;
                default: 
                    targetFov = _isAiming ? 48f : _hipFov; // ~1.25x iron-sight zoom (Glock 17)
                    break;
            }

            _targetCamera.fieldOfView = Mathf.Lerp(_targetCamera.fieldOfView, targetFov, dt * 14f);
        }

        private void HandleShootingAndReload()
        {
            int widx = (int)_currentWeapon;

            // 1. Reload Input: Desktop 'R'
            bool reloadTriggered = Input.GetKeyDown(KeyCode.R);

            if (reloadTriggered && !_isReloading && _ammoCurrent[widx] < AMMO_MAX[widx])
            {
                StartCoroutine(ReloadCoroutine());
                return;
            }

            // 2. Fire Input: Desktop Right Mouse Button (1) or Spacebar
            bool desktopFireHeld = Input.GetMouseButton(1) || Input.GetKey(KeyCode.Space);
            bool desktopFireDown = Input.GetMouseButtonDown(1) || Input.GetKeyDown(KeyCode.Space);

            // Ensure cursor is locked on click in Game View
            if ((desktopFireDown || Input.GetMouseButtonDown(0)) && !_cursorLocked)
            {
                ApplyCursorLock(true);
            }

            bool fireInput = IS_AUTO[widx] ? desktopFireHeld : desktopFireDown;

            if (fireInput && (_cursorLocked || Application.isEditor) && _weaponSwitchTimer <= 0f)
            {
                if (_isReloading) return;

                if (_ammoCurrent[widx] <= 0)
                {
                    if (desktopFireDown)
                    {
                        if (_dryFireClip != null && _weaponAudioSource != null)
                            _weaponAudioSource.PlayOneShot(_dryFireClip, 0.7f);
                    }
                    StartCoroutine(ReloadCoroutine());
                    return;
                }
                if (Time.time - _lastShootTime >= FIRE_RATES[widx])
                {
                    ExecuteWeaponShot();
                }
            }
        }

        private void ExecuteWeaponShot()
        {
            if (_targetCamera == null) EnsureCameraAndRig();
            if (_targetCamera == null) _targetCamera = Camera.main ?? GetComponent<Camera>();
            if (_targetCamera == null) return;

            int widx = (int)_currentWeapon;
            _lastShootTime = Time.time;
            _ammoCurrent[widx]--;
            _shotsFired++;

            // 1. Play Weapon Audio
            PlayWeaponShotAudio();

            // 2. Muzzle Flash
            if (_muzzleFlashLight != null) _muzzleFlashLight.enabled = true;
            if (_muzzleFlashQuad != null)
            {
                _muzzleFlashQuad.transform.localRotation = Quaternion.Euler(0f, 0f, UnityEngine.Random.Range(0f, 360f));
                _muzzleFlashQuad.SetActive(true);
            }
            _flashTimer = 0.05f;

            // 3. Dynamic Weapon Recoil Kick
            ApplyWeaponRecoil();

            // 4. Spent Casing Ejection
            EjectShellCasing();

            // 5. Weapon Ballistic Raycasts
            Vector3 baseOrigin;
            Vector3 baseForward;
            Vector3 baseRight;
            Vector3 baseUp;

            if (CurrentControlMode == InputControlMode.XRSimulator && _viewmodelRoot != null)
            {
                baseOrigin = _viewmodelRoot.position + _viewmodelRoot.forward * 0.15f;
                baseForward = _viewmodelRoot.forward;
                baseRight = _viewmodelRoot.right;
                baseUp = _viewmodelRoot.up;
            }
            else
            {
                baseOrigin = _targetCamera.transform.position;
                baseForward = _targetCamera.transform.forward;
                baseRight = _targetCamera.transform.right;
                baseUp = _targetCamera.transform.up;
            }

            // Point-blank obstruction check: prevent bullet from bypassing solid obstacle when muzzle is blocked
            if (Physics.Raycast(baseOrigin, baseForward, out RaycastHit pointBlankHit, 0.28f, ~0, QueryTriggerInteraction.Ignore))
            {
                float pointBlankDmg = _currentWeapon == WeaponType.CombatShotgun ? 25f * 8f : (_currentWeapon == WeaponType.AssaultRifleM4A1 ? 40f : 45f);
                ProcessHit(pointBlankHit, pointBlankDmg);

                if (_currentWeapon == WeaponType.CombatShotgun)
                {
                    StartCoroutine(ShotgunPumpCoroutine());
                }

                if (_rightDevice.isValid)
                {
                    uint channel = 0;
                    float hapticAmp = _currentWeapon == WeaponType.CombatShotgun ? 0.95f : (_currentWeapon == WeaponType.AssaultRifleM4A1 ? 0.65f : 0.75f);
                    _rightDevice.SendHapticImpulse(channel, hapticAmp, 0.08f);
                }

                if (_responseMechanism != null) _responseMechanism.ProcessResponse(null);
                else if (_rayPointer != null) _rayPointer.TriggerClick();

                Debug.Log(string.Format("[XR Firing Diagnostic] Point-blank shot IMPACTED solid obstacle '{0}' at {1:F2}m! Energy absorbed by obstacle surface.",
                    pointBlankHit.collider.name, pointBlankHit.distance));
                return;
            }

            if (_currentWeapon == WeaponType.CombatShotgun)
            {
                // Shotgun fires 8 buckshot pellets in spread cone (tighter choke when aiming)
                float shotgunSpread = _isAiming ? 0.018f : 0.042f;
                for (int i = 0; i < 8; i++)
                {
                    Vector2 spread = UnityEngine.Random.insideUnitCircle * shotgunSpread;
                    Vector3 pelletDir = (baseForward + baseRight * spread.x + baseUp * spread.y).normalized;
                    Ray ray = new Ray(baseOrigin, pelletDir);
                    if (Physics.Raycast(ray, out RaycastHit hit, 150f))
                    {
                        ProcessHit(hit, 25f);
                    }
                }
                // Racking pump action
                StartCoroutine(ShotgunPumpCoroutine());
            }
            else
            {
                // Single bullet (Pistol or Rifle) - accurate toward aiming point
                float spreadAmount = _currentWeapon == WeaponType.AssaultRifleM4A1 ? (_isAiming ? 0.0006f : 0.016f) : (_isAiming ? 0.0004f : 0.005f);
                Vector2 spread = UnityEngine.Random.insideUnitCircle * spreadAmount;
                Vector3 shootDir = (baseForward + baseRight * spread.x + baseUp * spread.y).normalized;
                Ray ray = new Ray(baseOrigin, shootDir);
                if (Physics.Raycast(ray, out RaycastHit hit, 200f))
                {
                    float dmg = _currentWeapon == WeaponType.AssaultRifleM4A1 ? 40f : 45f;
                    ProcessHit(hit, dmg);
                }
            }

            if (_rightDevice.isValid)
            {
                uint channel = 0;
                float hapticAmp = _currentWeapon == WeaponType.CombatShotgun ? 0.95f : (_currentWeapon == WeaponType.AssaultRifleM4A1 ? 0.65f : 0.75f);
                _rightDevice.SendHapticImpulse(channel, hapticAmp, 0.08f);
            }

            if (_responseMechanism != null) _responseMechanism.ProcessResponse(null);
            else if (_rayPointer != null) _rayPointer.TriggerClick();

            // Diagnostic Log: Shot executed with visual/audio validation
            if (_weaponAudioSource == null)
            {
                Debug.LogError("[XR Firing Diagnostic] Audio feedback MISSING: _weaponAudioSource is null!");
            }
            if (_muzzleFlashLight == null)
            {
                Debug.LogWarning("[XR Firing Diagnostic] Visual feedback MISSING: _muzzleFlashLight is null!");
            }

            Debug.Log(string.Format("[XR Firing Diagnostic] Shot EXECUTED for {0}! Remaining Ammo: {1}/{2} | AudioSource: {3} | FlashLight: {4} | RayOrigin: {5}",
                _currentWeapon,
                _ammoCurrent[widx],
                AMMO_MAX[widx],
                (_weaponAudioSource != null ? "Active" : "MISSING"),
                (_muzzleFlashLight != null ? "Active" : "MISSING"),
                baseOrigin));
        }

        private void PlayWeaponShotAudio()
        {
            if (_weaponAudioSource == null) return;

            switch (_currentWeapon)
            {
                case WeaponType.AssaultRifleM4A1:
                    _weaponAudioSource.pitch = UnityEngine.Random.Range(0.97f, 1.03f);
                    _weaponAudioSource.PlayOneShot(_rifleShotClip, 0.95f);
                    break;

                case WeaponType.CombatShotgun:
                    _weaponAudioSource.pitch = UnityEngine.Random.Range(0.95f, 1.02f);
                    _weaponAudioSource.PlayOneShot(_shotgunShotClip, 1.0f);
                    break;

                default: // Glock 17
                    if (_shotAudioClip != null)
                    {
                        _weaponAudioSource.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
                        _weaponAudioSource.PlayOneShot(_shotAudioClip, 0.95f);
                    }
                    break;
            }
        }

        private void ApplyWeaponRecoil()
        {
            switch (_currentWeapon)
            {
                case WeaponType.AssaultRifleM4A1:
                    _recoilOffset = new Vector3(0f, 0.018f, -0.048f);
                    _recoilRotation = Quaternion.Euler(-3.5f, UnityEngine.Random.Range(-0.8f, 0.8f), 0f);
                    break;

                case WeaponType.CombatShotgun:
                    _recoilOffset = new Vector3(0f, 0.055f, -0.11f);
                    _recoilRotation = Quaternion.Euler(-8.0f, UnityEngine.Random.Range(-1.2f, 1.2f), 0f);
                    _cameraShakeIntensity = 0.22f;
                    break;

                default: // Glock 17
                    _recoilOffset = new Vector3(0f, 0.024f, -0.065f);
                    _recoilRotation = Quaternion.Euler(-4.5f, UnityEngine.Random.Range(-0.6f, 0.6f), 0f);
                    break;
            }
        }

        private IEnumerator ShotgunPumpCoroutine()
        {
            yield return new WaitForSeconds(0.24f);

            // Slide back
            if (_shotgunPumpSlide != null)
                _shotgunPumpSlide.localPosition = new Vector3(0f, -0.005f, 0.18f);

            if (_shotgunPumpClip != null && _weaponAudioSource != null)
                _weaponAudioSource.PlayOneShot(_shotgunPumpClip, 0.85f);

            yield return new WaitForSeconds(0.22f);

            // Slide forward
            if (_shotgunPumpSlide != null)
                _shotgunPumpSlide.localPosition = new Vector3(0f, -0.005f, 0.24f);
        }

        private void EjectShellCasing()
        {
            if (_viewmodelRoot == null) return;

            int idx = _casingPoolIndex;
            _casingPoolIndex = (_casingPoolIndex + 1) % CASING_POOL_SIZE;

            GameObject casing = _casingPool[idx];
            Rigidbody rb = _casingRbs[idx];
            Renderer rend = _casingRenderers[idx];

            if (casing == null || rb == null) return;

            casing.SetActive(false);

            casing.transform.position = _viewmodelRoot.position + _viewmodelRoot.right * 0.04f + _viewmodelRoot.up * 0.02f;
            float scaleY = _currentWeapon == WeaponType.CombatShotgun ? 0.028f : 0.016f;
            float scaleR = _currentWeapon == WeaponType.CombatShotgun ? 0.014f : 0.009f;
            casing.transform.localScale = new Vector3(scaleR, scaleY, scaleR);
            casing.transform.rotation = Quaternion.Euler(UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360), UnityEngine.Random.Range(0, 360));

            if (rend != null)
            {
                rend.sharedMaterial = (_currentWeapon == WeaponType.CombatShotgun) ? _shotgunCasingMaterial : _brassMaterial;
            }

            casing.SetActive(true);

            Vector3 ejectDir = (_viewmodelRoot.right * 2.8f + _viewmodelRoot.up * 1.6f + _viewmodelRoot.forward * 0.1f);
            rb.linearVelocity = ejectDir + UnityEngine.Random.insideUnitSphere * 0.4f;
            rb.angularVelocity = UnityEngine.Random.insideUnitSphere * 25f;

            StartCoroutine(DeactivateAfterDelay(casing, 1.4f));
        }

        private void ProcessHit(RaycastHit hit, float damage)
        {
            var hitObj = hit.transform.gameObject;
            string objName = hitObj.name.ToLower();

            var popUpCtrl = hitObj.GetComponentInParent<IVRAR.Group02.Environment.TargetPopUpController>();
            bool isCivilian = (popUpCtrl != null && popUpCtrl.Classification == IVRAR.Group02.Environment.TargetClassification.CivilianFriendly) ||
                              objName.Contains("civilian") || objName.Contains("friendly");

            var hitZone = hitObj.GetComponent<EnemyHitZone>();
            bool isHeadshot = (hitZone != null && hitZone.ZoneType == HitZoneType.Head) || objName.Contains("head") || hit.point.y > 1.65f;
            bool isEnemy = !isCivilian && (objName.Contains("target") || objName.Contains("enemy") || objName.Contains("hostile") || objName.Contains("silhouette") || popUpCtrl != null || hitZone != null);

            var damageData = new DamageData
            {
                contactPoint = hit.point,
                normalPoint = Quaternion.LookRotation(hit.normal),
                force = _targetCamera.transform.forward * (damage * 1.2f)
            };

            // Route damage to specific hit zone or parent IDamageable
            var damageable = hitObj.GetComponent<IDamageable>() ?? hitObj.GetComponentInParent<IDamageable>();
            if (damageable != null)
            {
                damageable.Damage(damageData);
            }
            else
            {
                var humanTarget = hitObj.GetComponentInParent<HumanTarget>();
                if (humanTarget != null) humanTarget.Damage(damageData);
            }

            var tacticalTarget = hitObj.GetComponentInParent<IVRAR.Group02.TargetManagement.TacticalTarget>();
            if (tacticalTarget != null)
            {
                tacticalTarget.OnTargetHit();
            }

            var toggleBtn = hitObj.GetComponent<IVRAR.Group02.Environment.TacticalNightModeToggleButton>() ?? hitObj.GetComponentInParent<IVRAR.Group02.Environment.TacticalNightModeToggleButton>();
            if (toggleBtn != null)
            {
                toggleBtn.TriggerToggle();
            }

            if (isCivilian)
            {
                _score = Mathf.Max(0, _score - 150);
                _comboMultiplier = 1;
                _hitMarkerTimer = 0.35f;
                _lastHitText = "⚠ CEASE FIRE! CIVILIAN CASUALTY (-150 PTS) ⚠";
                _lastHitColor = Color.red;
                _hitTextTimer = 1.6f;
            }
            else if (isEnemy || damageable != null)
            {
                _totalHits++;
                _hitMarkerTimer = 0.25f;

                if (_comboTimer > 0f) _comboMultiplier = Mathf.Min(_comboMultiplier + 1, 5);
                else _comboMultiplier = 1;
                _comboTimer = COMBO_DURATION;

                int baseScore = isHeadshot ? 250 : 100;
                int awardedScore = baseScore * _comboMultiplier;
                _score += awardedScore;

                if (isHeadshot)
                {
                    _headshots++;
                    _lastHitText = $"★ HEADSHOT! +{awardedScore} PTS (x{_comboMultiplier}) ★";
                    _lastHitColor = new Color(1f, 0.85f, 0.1f);
                }
                else
                {
                    _lastHitText = $"TARGET HIT! +{awardedScore} PTS (x{_comboMultiplier})";
                    _lastHitColor = new Color(0.2f, 1f, 0.4f);
                }
                _hitTextTimer = 1.2f;
            }

            CreateHitSparks(hit, isEnemy);
        }

        private void CreateHitSparks(RaycastHit hit, bool isEnemy)
        {
            int idx = _sparkPoolIndex;
            _sparkPoolIndex = (_sparkPoolIndex + 1) % SPARK_POOL_SIZE;

            GameObject spark = _sparkPool[idx];
            Renderer rend = _sparkRenderers[idx];
            if (spark == null) return;

            spark.SetActive(false);
            spark.transform.position = hit.point + hit.normal * 0.02f;

            if (rend != null)
            {
                rend.sharedMaterial = isEnemy ? _sparkEnemyMaterial : _sparkTargetMaterial;
            }

            spark.SetActive(true);
            StartCoroutine(DeactivateAfterDelay(spark, 0.25f));
        }

        private IEnumerator DeactivateAfterDelay(GameObject obj, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (obj != null) obj.SetActive(false);
        }

        private void HandleBarrelExploded(Vector3 blastPos, int targetsHit)
        {
            _cameraShakeIntensity = 0.45f;
            int bonus = 500 + targetsHit * 150;
            _score += bonus;
            _lastHitText = $"💥 HAZMAT BARREL DETONATION! +{bonus} PTS ({targetsHit} KILLS) 💥";
            _lastHitColor = new Color(1f, 0.55f, 0.1f);
            _hitTextTimer = 2.0f;
        }

        private void HandleEnemyKilled(IVRAR.Group02.Environment.MovingEnemyController enemy, bool headshot, int pts)
        {
            _score += pts * _comboMultiplier;
            _lastHitText = headshot ? $"★ TACTICAL HEADSHOT ELIMINATION! +{pts * _comboMultiplier} ★" : $"ENEMY NEUTRALIZED! +{pts * _comboMultiplier}";
            _lastHitColor = headshot ? Color.yellow : Color.green;
            _hitTextTimer = 1.4f;
        }

        private IEnumerator ReloadCoroutine()
        {
            _isReloading = true;
            int widx = (int)_currentWeapon;

            if (_magOutClip != null && _weaponAudioSource != null)
            {
                _weaponAudioSource.PlayOneShot(_magOutClip, 0.85f);
            }

            float reloadDuration = _currentWeapon == WeaponType.AssaultRifleM4A1 ? 1.6f : (_currentWeapon == WeaponType.CombatShotgun ? 2.0f : 1.2f);

            // Smooth viewmodel reload lower animation
            _recoilOffset += new Vector3(0f, -0.12f, -0.04f);

            yield return new WaitForSeconds(reloadDuration * 0.45f);

            if (_magInClip != null && _weaponAudioSource != null)
            {
                _weaponAudioSource.PlayOneShot(_magInClip, 0.85f);
            }
            yield return new WaitForSeconds(reloadDuration * 0.35f);

            if (_slideForwardClip != null && _weaponAudioSource != null)
            {
                _weaponAudioSource.PlayOneShot(_slideForwardClip, 0.85f);
            }
            yield return new WaitForSeconds(reloadDuration * 0.20f);

            _ammoCurrent[widx] = AMMO_MAX[widx];
            _isReloading = false;
        }

        private void HandleTacticalAttachments()
        {
            if (Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.L))
            {
                ToggleLaser();
            }

            if (Input.GetKeyDown(KeyCode.F))
            {
                ToggleFlashlight();
            }

            if (Input.GetKeyDown(KeyCode.O))
            {
                ToggleObservationOptic();
            }

            if (Input.GetKeyDown(KeyCode.G))
            {
                var drill = IVRAR.Group02.Environment.TacticalDrillGameManager.Instance;
                if (drill != null)
                {
                    if (drill.CurrentState == IVRAR.Group02.Environment.DrillState.ActiveDrill)
                    {
                        drill.EndDrill();
                    }
                    else
                    {
                        drill.StartCountdown();
                    }
                }
            }
        }

        private void UpdateWeaponSwayAndRecoil()
        {
            if (_viewmodelRoot == null) return;
            ApplyWeaponParenting();

            // Weapon Obstruction & Proximity Penetration Check
            float maxReach = 0.50f;
            switch (_currentWeapon)
            {
                case WeaponType.AssaultRifleM4A1: maxReach = 0.74f; break;
                case WeaponType.CombatShotgun: maxReach = 0.80f; break;
                default: maxReach = 0.50f; break;
            }
            if (_isAiming) maxReach -= 0.08f;

            float targetObstructionOffset = 0f;
            float targetObstructionPitch = 0f;
            _isWeaponObstructed = false;

            if (_enableWeaponObstruction && _targetCamera != null)
            {
                Vector3 rayOrigin = _targetCamera.transform.position;
                Vector3 rayDir = (_viewmodelRoot != null) ? _viewmodelRoot.forward : _targetCamera.transform.forward;

                if (Physics.SphereCast(rayOrigin, 0.04f, rayDir, out RaycastHit obsHit, maxReach, ~0, QueryTriggerInteraction.Ignore))
                {
                    float penetration = Mathf.Max(0f, maxReach - obsHit.distance);
                    targetObstructionOffset = Mathf.Min(penetration, maxReach - 0.12f);
                    targetObstructionPitch = Mathf.Clamp(penetration * 48f, 0f, 25f);
                    if (targetObstructionOffset > 0.02f || obsHit.distance < 0.28f)
                    {
                        _isWeaponObstructed = true;
                    }
                }
            }

            float dt = Time.deltaTime > 0.0001f ? Time.deltaTime : 1.0f;
            _currentObstructionOffset = Mathf.Lerp(_currentObstructionOffset, targetObstructionOffset, Mathf.Clamp01(dt * 18f));
            _currentObstructionPitch = Mathf.Lerp(_currentObstructionPitch, targetObstructionPitch, Mathf.Clamp01(dt * 18f));

            if (!UseCameraRelativeWeaponPresentation)
            {
                // Physical Quest Headset mode: 6DoF natural hand tracking under _weaponMount
                if (_weaponMount != null)
                {
                    _viewmodelRoot.localPosition = _recoilOffset + new Vector3(0f, -_currentObstructionOffset * 0.35f, -_currentObstructionOffset);
                    _viewmodelRoot.localRotation = Quaternion.Euler(-_currentObstructionPitch, 0f, 0f) * _recoilRotation;
                }
            }
            else
            {
                // Camera-relative presentation (Desktop Gameplay + Meta XR Simulator)
                // Weapon is parented to _targetCamera.transform so locomotion, turning, and camera movement
                // move the camera and gun together in perfect synchrony without floating or drifting.
                if (_targetCamera == null) return;

                Vector3 targetHipPos;
                Vector3 targetAimPos;

                switch (_currentWeapon)
                {
                    case WeaponType.AssaultRifleM4A1:
                        targetHipPos = new Vector3(0.18f, -0.16f, 0.42f);
                        // Holographic optic reticle aligns with camera eye level
                        targetAimPos = new Vector3(0.0f, -0.0699f, 0.35f);
                        break;
                    case WeaponType.CombatShotgun:
                        targetHipPos = new Vector3(0.19f, -0.17f, 0.44f);
                        // Front bead sight aligns with camera eye level
                        targetAimPos = new Vector3(0.0f, -0.0485f, 0.36f);
                        break;
                    default: // Glock 17
                        targetHipPos = new Vector3(0.18f, -0.16f, 0.38f);
                        // Tritium rear notch and front post align with camera eye level
                        targetAimPos = new Vector3(0.0f, -0.0454f, 0.32f);
                        break;
                }

                Vector3 basePos = _isAiming ? targetAimPos : targetHipPos;

                float bobX = Mathf.Cos(_bobTimer) * 0.008f;
                float bobY = Mathf.Sin(_bobTimer * 2f) * 0.007f;
                if (_isAiming) { bobX *= 0.10f; bobY *= 0.10f; }

                Vector3 shakeOffset = Vector3.zero;
                if (_cameraShakeIntensity > 0f)
                {
                    shakeOffset = UnityEngine.Random.insideUnitSphere * (_cameraShakeIntensity * 0.02f);
                    _cameraShakeIntensity = Mathf.MoveTowards(_cameraShakeIntensity, 0f, dt * 3.5f);
                }

                Vector3 targetPos = basePos + new Vector3(bobX, bobY, 0f) + _recoilOffset + shakeOffset + new Vector3(0f, -_currentObstructionOffset * 0.35f, -_currentObstructionOffset);
                Quaternion targetRot = (_isAiming ? Quaternion.identity : Quaternion.Euler(1.5f, -2.5f, 0f)) * _recoilRotation;
                targetRot = Quaternion.Euler(-_currentObstructionPitch, 0f, 0f) * targetRot;
                if (shakeOffset.sqrMagnitude > 0.0001f)
                {
                    targetRot *= Quaternion.Euler(shakeOffset.y * 40f, shakeOffset.x * 40f, 0f);
                }

                _viewmodelRoot.localPosition = Vector3.Lerp(_viewmodelRoot.localPosition, targetPos, dt * 24f);
                _viewmodelRoot.localRotation = Quaternion.Slerp(_viewmodelRoot.localRotation, targetRot, dt * 24f);
            }

            // Recover recoil
            _recoilOffset = Vector3.Lerp(_recoilOffset, Vector3.zero, dt * 14f);
            _recoilRotation = Quaternion.Slerp(_recoilRotation, Quaternion.identity, dt * 14f);

            // Flash timeout
            if (_flashTimer > 0f)
            {
                _flashTimer -= Time.deltaTime;
                if (_flashTimer <= 0f)
                {
                    if (_muzzleFlashLight != null) _muzzleFlashLight.enabled = false;
                    if (_muzzleFlashQuad != null) _muzzleFlashQuad.SetActive(false);
                }
            }
        }

        public void UpdateLaserSight()
        {
            if (!_laserActive || _laserLine == null || _currentWeapon != WeaponType.Glock17)
            {
                if (_laserLine != null && _laserLine.enabled) _laserLine.enabled = false;
                if (_laserDot != null && _laserDot.activeSelf) _laserDot.SetActive(false);
                return;
            }

            if (!_laserLine.enabled) _laserLine.enabled = true;

            Transform emitter = _glockModel != null ? _glockModel.transform.Find("LaserLightUnit") : null;
            Vector3 origin = emitter != null
                ? emitter.position + emitter.forward * 0.04f
                : (_laserLine.transform.position + (_viewmodelRoot != null ? _viewmodelRoot.forward * 0.035f : Vector3.zero));
            Vector3 forward = emitter != null
                ? emitter.forward
                : (_viewmodelRoot != null ? _viewmodelRoot.forward : (_targetCamera != null ? _targetCamera.transform.forward : Vector3.forward));

            _laserLine.SetPosition(0, origin);

            if (Physics.Raycast(origin, forward, out RaycastHit hit, 80f))
            {
                _laserLine.SetPosition(1, hit.point);
                if (_laserDot != null)
                {
                    _laserDot.SetActive(true);
                    _laserDot.transform.position = hit.point - forward * 0.02f;
                    _laserDot.transform.rotation = Quaternion.LookRotation(hit.normal);
                }
            }
            else
            {
                _laserLine.SetPosition(1, origin + forward * 80f);
                if (_laserDot != null) _laserDot.SetActive(false);
            }
        }

        private void UpdateCombosAndEffects()
        {
            if (_comboTimer > 0f)
            {
                _comboTimer -= Time.deltaTime;
                if (_comboTimer <= 0f) _comboMultiplier = 1;
            }

            if (_hitMarkerTimer > 0f) _hitMarkerTimer -= Time.deltaTime;
            if (_hitTextTimer > 0f) _hitTextTimer -= Time.deltaTime;
        }

        #endregion

        #region Procedural Audio Generators

        private AudioClip GenerateRifleShotClip()
        {
            int sampleRate = 44100;
            float duration = 0.18f;
            int numSamples = (int)(sampleRate * duration);
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Exp(-t * 22f);
                float crack = Mathf.Sin(2f * Mathf.PI * 340f * t * (1f - t * 0.4f));
                float noise = (UnityEngine.Random.value * 2f - 1f) * 0.75f;
                samples[i] = (crack * 0.55f + noise * 0.45f) * env;
            }

            AudioClip clip = AudioClip.Create("RifleShot", numSamples, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private AudioClip GenerateShotgunShotClip()
        {
            int sampleRate = 44100;
            float duration = 0.48f;
            int numSamples = (int)(sampleRate * duration);
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Exp(-t * 11f);
                float punch = Mathf.Sin(2f * Mathf.PI * 95f * t * (1f - t * 0.6f));
                float blastNoise = (UnityEngine.Random.value * 2f - 1f) * 0.85f;
                samples[i] = (punch * 0.65f + blastNoise * 0.55f) * env;
            }

            AudioClip clip = AudioClip.Create("ShotgunShot", numSamples, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private AudioClip GeneratePumpActionClip()
        {
            int sampleRate = 44100;
            float duration = 0.16f;
            int numSamples = (int)(sampleRate * duration);
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Exp(-t * 26f);
                float metallic = Mathf.Sin(2f * Mathf.PI * 880f * t);
                samples[i] = metallic * env * 0.7f;
            }

            AudioClip clip = AudioClip.Create("ShotgunPump", numSamples, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private AudioClip GenerateDryFireClip()
        {
            int sampleRate = 44100;
            float duration = 0.08f;
            int numSamples = (int)(sampleRate * duration);
            float[] samples = new float[numSamples];

            for (int i = 0; i < numSamples; i++)
            {
                float t = (float)i / sampleRate;
                float env = Mathf.Exp(-t * 60f);
                float click = Mathf.Sin(2f * Mathf.PI * 1800f * t);
                samples[i] = click * env * 0.85f;
            }

            AudioClip clip = AudioClip.Create("DryFire", numSamples, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        #endregion

        #region Public API

        public void SetDesktopMode(bool active)
        {
            SetControlMode(active ? InputControlMode.DesktopGameplay : InputControlMode.XRSimulator);
        }

        #endregion

        #region HUD & Esports Overlay

        private static GUIStyle s_ScoreStyle;
        private static GUIStyle s_SubStyle;
        private static GUIStyle s_ComboStyle;
        private static GUIStyle s_HitStyle;
        private static GUIStyle s_DockStyle;
        private static GUIStyle s_HelpHeaderStyle;
        private static GUIStyle s_HelpBodyStyle;
        private static GUIStyle s_ModeBadgeStyle;
        private static GUIStyle s_DrillStyle;
        private static GUIStyle s_DrillCdStyle;
        private static GUIStyle s_ReportHeaderStyle;
        private static GUIStyle s_ReportRankStyle;
        private static GUIStyle s_ReportItemStyle;
        private static GUIStyle s_ReportPromptStyle;

        private static void EnsureGUIStyles()
        {
            if (s_ScoreStyle != null) return;

            s_ScoreStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            s_SubStyle = new GUIStyle(GUI.skin.label) { fontSize = 11, alignment = TextAnchor.MiddleLeft };
            s_ComboStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold };
            s_HitStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_DockStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_HelpHeaderStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold };
            s_HelpBodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            s_ModeBadgeStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_DrillStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_DrillCdStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 42,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_ReportHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_ReportRankStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 44,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            s_ReportItemStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleLeft };
            s_ReportPromptStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
        }

        private void OnGUI()
        {
            EnsureGUIStyles();

            DrawModeBadge();
            DrawWeaponDock();
            DrawReticleAndHitMarker();
            DrawCombatScore();
            DrawFloatingHitText();
            DrawTacticalDrillHUD();

            // Accessible In-Game Day/Night Toggle Button in Top-Right
            var envMgr = IVRAR.Group02.Environment.LowVisibilityEnvironmentManager.Instance;
            bool isNight = envMgr != null && envMgr.IsNightMode;
            string nightBtnText = isNight ? "🌙 NIGHT MODE ACTIVE [Click / N / ~ / F4]" : "☀ DAY MODE ACTIVE [Click / N / ~ / F4]";
            GUI.color = isNight ? new Color(0.2f, 0.45f, 0.95f, 0.95f) : new Color(0.95f, 0.75f, 0.2f, 0.95f);
            if (GUI.Button(new Rect(Screen.width - 295, 10, 280, 26), nightBtnText))
            {
                ToggleNightMode();
            }

            // Unobtrusive Obstruction Indicator
            if (_isWeaponObstructed)
            {
                GUI.color = new Color(1f, 0.65f, 0.1f, 0.90f);
                GUI.Label(new Rect(Screen.width / 2f - 120, Screen.height - 105, 240, 22), "⚠ WEAPON OBSTRUCTED (RETRACTED)", s_DockStyle);
            }

            if (_isObservationOpticActive)
            {
                DrawObservationSpottingOpticOverlay();
            }

            if (_showHelpOverlay)
            {
                DrawKeybindHelp();
            }
        }

        private void DrawModeBadge()
        {
            float cx = Screen.width / 2f;
            float w = 400;
            float h = 26;
            float x = cx - w / 2f;
            float y = 10;

            bool isDesktop = (CurrentControlMode == InputControlMode.DesktopGameplay);
            GUI.color = isDesktop ? new Color(0.1f, 0.40f, 0.85f, 0.90f) : new Color(0.2f, 0.70f, 0.35f, 0.90f);
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);

            GUI.color = Color.white;
            string badgeText = isDesktop
                ? "MODE: DESKTOP GAMEPLAY [F1: Switch to XR Simulator]"
                : "MODE: XR SIMULATOR [F1: Switch to Desktop Gameplay]";
            GUI.Label(new Rect(x, y + 2, w, h - 4), badgeText, s_ModeBadgeStyle);
        }

        private void DrawWeaponDock()
        {
            float w = 420;
            float h = 64;
            float x = (Screen.width - w) / 2f;
            float y = Screen.height - h - 16;

            GUI.color = new Color(0.05f, 0.07f, 0.10f, 0.88f);
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);

            string[] weaponNames = { "[1] GLOCK 17", "[2] M4A1 CARBINE", "[3] REMINGTON 870" };
            float colW = w / 3f;

            for (int i = 0; i < 3; i++)
            {
                bool isSelected = (int)_currentWeapon == i;
                GUI.color = isSelected ? new Color(1.0f, 0.85f, 0.2f, 1f) : new Color(0.6f, 0.65f, 0.7f, 0.8f);

                Rect slotRect = new Rect(x + i * colW, y + 4, colW, 26);
                GUI.Label(slotRect, weaponNames[i], s_DockStyle);

                // Ammo subtext
                string ammoStr = $"{_ammoCurrent[i]} / {AMMO_MAX[i]}";
                if (_isReloading && isSelected) ammoStr = "RELOADING...";
                GUI.color = isSelected ? Color.white : new Color(0.5f, 0.5f, 0.5f, 0.7f);
                GUI.Label(new Rect(x + i * colW, y + 30, colW, 24), ammoStr, s_DockStyle);
            }
        }

        private void DrawReticleAndHitMarker()
        {
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;

            // Reticle
            if (_isAiming)
            {
                if (_currentWeapon == WeaponType.AssaultRifleM4A1 && CurrentControlMode == InputControlMode.DesktopGameplay)
                {
                    // Centered Holographic Red-Dot Optic Reticle
                    GUI.color = new Color(1.0f, 0.15f, 0.15f, 0.95f);
                    // Center Dot (3x3)
                    GUI.DrawTexture(new Rect(cx - 1.5f, cy - 1.5f, 3, 3), Texture2D.whiteTexture);
                    // Holographic Outer Ring Crosshairs (4 fine cross ticks around lens)
                    GUI.DrawTexture(new Rect(cx - 18, cy - 0.75f, 8, 1.5f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx + 10, cy - 0.75f, 8, 1.5f), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 0.75f, cy - 18, 1.5f, 8), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 0.75f, cy + 10, 1.5f, 8), Texture2D.whiteTexture);
                }
            }
            else
            {
                GUI.color = new Color(0.2f, 1.0f, 0.4f, 0.85f);
                if (_currentWeapon == WeaponType.CombatShotgun)
                {
                    // Circle reticle for shotgun
                    GUI.Box(new Rect(cx - 16, cy - 16, 32, 32), GUIContent.none);
                    GUI.DrawTexture(new Rect(cx - 2, cy - 2, 4, 4), Texture2D.whiteTexture);
                }
                else
                {
                    // Precision Crosshair
                    float spread = _currentWeapon == WeaponType.AssaultRifleM4A1 ? 14f : 8f;
                    GUI.DrawTexture(new Rect(cx - spread - 12, cy - 1.5f, 10, 3), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx + spread + 2, cy - 1.5f, 10, 3), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 1.5f, cy - spread - 12, 3, 10), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 1.5f, cy + spread + 2, 3, 10), Texture2D.whiteTexture);
                    GUI.DrawTexture(new Rect(cx - 2, cy - 2, 4, 4), Texture2D.whiteTexture);
                }
            }

            // Hit Marker (X indicator)
            if (_hitMarkerTimer > 0f)
            {
                GUI.color = Color.red;
                GUI.DrawTexture(new Rect(cx - 10, cy - 10, 5, 5), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + 6, cy - 10, 5, 5), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx - 10, cy + 6, 5, 5), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(cx + 6, cy + 6, 5, 5), Texture2D.whiteTexture);
            }
        }

        private void DrawCombatScore()
        {
            float x = 20;
            float y = 96;
            float w = 310;
            float h = 64;

            GUI.color = new Color(0.04f, 0.06f, 0.08f, 0.85f);
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);

            float accuracy = _shotsFired > 0 ? ((float)_totalHits / _shotsFired * 100f) : 100f;
            GUI.color = new Color(0.85f, 0.90f, 0.95f);
            GUI.Label(new Rect(x + 12, y + 6, w - 24, 20), $"BALLISTIC TELEMETRY: ACCURACY {accuracy:F1}%", s_ScoreStyle);

            GUI.color = new Color(0.70f, 0.75f, 0.80f);
            GUI.Label(new Rect(x + 12, y + 26, w - 24, 18), $"ROUNDS FIRED: {_shotsFired}  |  CONFIRMED HITS: {_totalHits}", s_SubStyle);
            GUI.Label(new Rect(x + 12, y + 42, w - 24, 18), $"THORACIC / CRANIAL IMPACTS: {_headshots}", s_SubStyle);
        }

        private void DrawFloatingHitText()
        {
            if (_hitTextTimer <= 0f) return;

            // Restrained, professional military target registration label
            float cx = Screen.width / 2f;
            float cy = Screen.height * 0.76f;

            GUI.color = new Color(0.35f, 0.85f, 0.55f, Mathf.Clamp01(_hitTextTimer * 1.5f));
            GUIStyle restrainedStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(cx - 200, cy, 400, 24), "[TARGET IMPACT CONFIRMED]", restrainedStyle);
        }

        private void DrawObservationSpottingOpticOverlay()
        {
            float cx = Screen.width / 2f;
            float cy = Screen.height / 2f;
            float lensRadius = Mathf.Min(Screen.width, Screen.height) * 0.42f;

            // Optical Glass Center Tint
            GUI.color = new Color(0.85f, 0.95f, 1.0f, 0.04f);
            GUI.DrawTexture(new Rect(cx - lensRadius, cy - lensRadius, lensRadius * 2, lensRadius * 2), Texture2D.whiteTexture);

            // Mil-Dot Observation Crosshairs
            GUI.color = new Color(0.15f, 0.20f, 0.25f, 0.85f);
            // Horizontal line
            GUI.DrawTexture(new Rect(cx - lensRadius * 0.85f, cy - 0.75f, lensRadius * 1.7f, 1.5f), Texture2D.whiteTexture);
            // Vertical line
            GUI.DrawTexture(new Rect(cx - 0.75f, cy - lensRadius * 0.85f, 1.5f, lensRadius * 1.7f), Texture2D.whiteTexture);

            // Stadiametric Range Finder Brackets (10m, 15m, 20m, 25m)
            float[] rangeDistances = { 10f, 15f, 20f, 25f };
            float[] bracketHeights = { 42f, 28f, 21f, 17f };
            float[] bracketOffsets = { -70f, -40f, -15f, 10f };

            GUI.color = new Color(0.12f, 0.16f, 0.20f, 0.90f);
            for (int i = 0; i < rangeDistances.Length; i++)
            {
                float bx = cx + bracketOffsets[i];
                float bh = bracketHeights[i];
                GUI.DrawTexture(new Rect(bx, cy + 18, 14, 1.5f), Texture2D.whiteTexture);
                GUI.DrawTexture(new Rect(bx, cy + 18 - bh, 14, 1.5f), Texture2D.whiteTexture);
            }

            // Spotting Optic Telemetry Banner
            GUI.color = new Color(0.06f, 0.08f, 0.10f, 0.88f);
            GUI.Box(new Rect(cx - 240, 48, 480, 42), GUIContent.none);

            GUI.color = Color.white;
            GUIStyle obsHeader = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(cx - 240, 52, 480, 18), "3.5x MAGNIFIED OBSERVATION SPOTTER — STADIAMETRIC TARGET DISCRIMINATION", obsHeader);

            GUI.color = new Color(0.7f, 0.8f, 0.9f);
            GUIStyle obsSub = new GUIStyle(GUI.skin.label)
            {
                fontSize = 9,
                alignment = TextAnchor.MiddleCenter
            };
            GUI.Label(new Rect(cx - 240, 68, 480, 18), "STADIAMETRIC RANGE: 10M-25M | PRESS [O] TO RETURN TO STANDARD VIEW", obsSub);
        }

        private void DrawKeybindHelp()
        {
            float w = 290;
            float h = 248;
            float x = Screen.width - w - 20;
            float y = 20;

            GUI.color = new Color(0.04f, 0.06f, 0.08f, 0.88f);
            GUI.Box(new Rect(x, y, w, h), GUIContent.none);

            GUI.color = Color.yellow;
            GUI.Label(new Rect(x + 12, y + 8, w - 24, 20), "TACTICAL CONTROLS", s_HelpHeaderStyle);

            GUI.color = Color.white;
            float lineY = y + 30;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• [F1]: Toggle Desktop / XR Simulator Mode", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• WASD + Shift: Move & Sprint", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• Mouse Look: Rotate Camera", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• Scroll Wheel / [1][2][3]: Switch Weapons", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• LMB (Hold): Aim Down Sights / Zoom", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• RMB: Shoot Weapon", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• R: Reload Magazine", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• O: Spotter Optic | V: Night Vision", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• T: Laser | F: Flashlight | N/~: Night Mode | V: NVG", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• XR Left Ctrl: [X] / [Y] Button -> Night Mode", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• Tab / M: Training Terminal Menu", s_HelpBodyStyle); lineY += 19;
            GUI.Label(new Rect(x + 12, lineY, w - 24, 18), "• Esc: Release Cursor | Click: Lock Cursor", s_HelpBodyStyle);
        }

        private void DrawTacticalDrillHUD()
        {
            var drillMgr = IVRAR.Group02.Environment.TacticalDrillGameManager.Instance;
            if (drillMgr == null) return;

            float cx = Screen.width / 2f;

            if (drillMgr.CurrentState == IVRAR.Group02.Environment.DrillState.Countdown)
            {
                GUI.color = Color.yellow;
                int cdNum = Mathf.CeilToInt(drillMgr.CountdownTimer);
                string cdMsg = cdNum > 0 ? $"READY... {cdNum}" : "ENGAGE!";
                GUI.Label(new Rect(cx - 200, Screen.height * 0.35f, 400, 60), cdMsg, s_DrillCdStyle);
            }
            else if (drillMgr.CurrentState == IVRAR.Group02.Environment.DrillState.ActiveDrill)
            {
                GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.90f);
                GUI.Box(new Rect(cx - 260, 15, 520, 48), GUIContent.none);

                GUI.color = Color.white;
                string statLine = $"TIME: {drillMgr.TimeRemaining:F1}s  |  SCORE: {drillMgr.DrillScore}  |  HOSTILES: {drillMgr.HostilesNeutralized}  |  CIVILIANS SPARED: {drillMgr.CiviliansSpared}  |  d': {drillMgr.DPrimeScore:F2}";
                GUI.Label(new Rect(cx - 250, 25, 500, 28), statLine, s_DrillStyle);
            }
            else if (drillMgr.CurrentState == IVRAR.Group02.Environment.DrillState.FreePractice)
            {
                GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.75f);
                GUI.Box(new Rect(cx - 210, 15, 420, 36), GUIContent.none);

                GUI.color = Color.cyan;
                GUI.Label(new Rect(cx - 200, 20, 400, 24), "FREE PRACTICE: PRESS [G] TO START 45s DRILL  |  [N] NIGHT-OPS", s_DockStyle);
            }
            else if (drillMgr.CurrentState == IVRAR.Group02.Environment.DrillState.AfterActionReport)
            {
                float w = 480;
                float h = 330;
                float left = cx - w / 2f;
                float top = Screen.height / 2f - h / 2f;

                GUI.color = new Color(0.04f, 0.06f, 0.09f, 0.96f);
                GUI.Box(new Rect(left, top, w, h), GUIContent.none);

                GUI.color = Color.yellow;
                GUI.Label(new Rect(left, top + 15, w, 28), "★ TACTICAL DRILL AFTER-ACTION REPORT ★", s_ReportHeaderStyle);

                string rank = drillMgr.GetDrillRank();
                Color rankCol = rank == "S" ? new Color(1f, 0.85f, 0.1f) : (rank == "A" ? Color.green : (rank == "B" ? Color.cyan : Color.white));
                GUI.color = rankCol;
                GUI.Label(new Rect(left, top + 45, w, 55), $"GRADE: {rank}", s_ReportRankStyle);

                GUI.color = Color.white;
                float y = top + 105;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Final Combat Score: {drillMgr.DrillScore} PTS", s_ReportItemStyle); y += 24;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Hostiles Neutralized: {drillMgr.HostilesNeutralized}  |  Missed: {drillMgr.HostilesMissed}", s_ReportItemStyle); y += 24;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Civilian Casualties: {drillMgr.CivilianCasualties}  |  Spared: {drillMgr.CiviliansSpared}", s_ReportItemStyle); y += 24;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Average Reaction Latency: {drillMgr.AverageReactionLatencyMs:F1} ms", s_ReportItemStyle); y += 24;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Signal Detection (d-prime): {drillMgr.DPrimeScore:F2}", s_ReportItemStyle); y += 24;
                GUI.Label(new Rect(left + 50, y, 380, 22), $"• Headshots: {drillMgr.HeadshotCount}", s_ReportItemStyle); y += 30;

                GUI.color = Color.cyan;
                GUI.Label(new Rect(left, y, w, 26), "PRESS [G] TO START NEW DRILL  |  PRESS [N] TO TOGGLE NIGHT-OPS", s_ReportPromptStyle);
            }
        }



        #endregion
    }
}
