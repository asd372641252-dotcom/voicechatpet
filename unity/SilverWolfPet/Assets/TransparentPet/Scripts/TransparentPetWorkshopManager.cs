using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class TransparentPetWorkshopManager : MonoBehaviour
{
    public enum WorkshopItemType
    {
        Model,
        Scene,
        Action,
        Unknown
    }

    [Serializable]
    public sealed class WorkshopItem
    {
        public string Id;
        public string Name;
        public WorkshopItemType Type;
        public string RootPath;
        public string ManifestPath;
        public string Entry;
        public string EntryPath;
        public string Thumbnail;
        public string ThumbnailPath;
        public string BundleAssetName;
        public string Format;
        public string Status;
        public bool CanApply;
    }

    public Transform modelRoot;
    public TransparentPetRuntimeControls runtimeControls;
    public TransparentPetKawaiiActionController actionController;
    public TransparentPetHeadLookAt headLookAt;
    public TransparentPetSkeletonHitMask skeletonHitMask;
    public PetExpressionController expressionController;
    public PetBlinkController blinkController;
    public TransparentWindowController windowController;
    public Camera targetCamera;
    public string[] extraWorkshopRoots = Array.Empty<string>();
    public string persistentWorkshopFolderName = "Workshop";
    public string streamingWorkshopFolderName = "Workshop";
    public string steamAppId = "";
    public bool scanOnStart = true;
    public bool applySavedModelOnStart = true;

    private const string PreferencePrefix = "voicechatpet.Workshop.";
    private const string SelectedModelKey = PreferencePrefix + "SelectedModel.v1";
    private const string SelectedSceneKey = PreferencePrefix + "SelectedScene.v1";
    private const string SelectedActionKey = PreferencePrefix + "SelectedAction.v1";

    private readonly List<WorkshopItem> _items = new List<WorkshopItem>();
    private readonly List<WorkshopItem> _models = new List<WorkshopItem>();
    private readonly List<WorkshopItem> _scenes = new List<WorkshopItem>();
    private readonly List<WorkshopItem> _actions = new List<WorkshopItem>();
    private GameObject _loadedModelInstance;
    private string _selectedModelId = string.Empty;
    private string _selectedSceneId = string.Empty;
    private string _selectedActionId = string.Empty;
    private string _status = "Workshop not scanned";

    public IReadOnlyList<WorkshopItem> Items => _items;
    public IReadOnlyList<WorkshopItem> Models => _models;
    public IReadOnlyList<WorkshopItem> Scenes => _scenes;
    public IReadOnlyList<WorkshopItem> Actions => _actions;
    public string Status => _status;
    public string SelectedModelId => _selectedModelId;
    public string SelectedSceneId => _selectedSceneId;
    public string SelectedActionId => _selectedActionId;

    private void Awake()
    {
        ResolveReferences();
        LoadSelectedIds();
    }

    private void Start()
    {
        if (!scanOnStart)
        {
            return;
        }

        Refresh();
        if (applySavedModelOnStart && !string.IsNullOrWhiteSpace(_selectedModelId))
        {
            ApplyModel(_selectedModelId);
        }
    }

    public void Refresh()
    {
        _items.Clear();
        _models.Clear();
        _scenes.Clear();
        _actions.Clear();

        HashSet<string> visitedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] roots = BuildScanRoots();
        for (int i = 0; i < roots.Length; i++)
        {
            string root = NormalizePath(roots[i]);
            if (string.IsNullOrWhiteSpace(root) || !visitedRoots.Add(root) || !Directory.Exists(root))
            {
                continue;
            }

            ScanRoot(root);
        }

        _status = "Found " + _items.Count + " Workshop item(s)";
    }

    public bool ApplyModel(string itemId)
    {
        ResolveReferences();
        WorkshopItem item = FindItem(_models, itemId);
        if (item == null)
        {
            _status = "Model item not found: " + itemId;
            return false;
        }

        _selectedModelId = item.Id;
        PlayerPrefs.SetString(SelectedModelKey, _selectedModelId);
        PlayerPrefs.Save();

        if (!item.CanApply)
        {
            _status = item.Name + " selected. " + item.Status;
            return false;
        }

        string extension = Path.GetExtension(item.EntryPath).ToLowerInvariant();
        if (extension == ".assetbundle" || extension == ".bundle")
        {
            bool loaded = TryApplyModelAssetBundle(item);
            _status = loaded ? "Applied model: " + item.Name : "Could not load model bundle: " + item.Name;
            return loaded;
        }

        _status = item.Name + " selected. Runtime importer is not installed for " + extension;
        return false;
    }

    public bool SelectScene(string itemId)
    {
        WorkshopItem item = FindItem(_scenes, itemId);
        if (item == null)
        {
            _status = "Scene item not found: " + itemId;
            return false;
        }

        _selectedSceneId = item.Id;
        PlayerPrefs.SetString(SelectedSceneKey, _selectedSceneId);
        PlayerPrefs.Save();
        _status = item.Name + " selected. Scene hot-swap loader will use this package when installed.";
        return true;
    }

    public bool SelectAction(string itemId)
    {
        WorkshopItem item = FindItem(_actions, itemId);
        if (item == null)
        {
            _status = "Action item not found: " + itemId;
            return false;
        }

        _selectedActionId = item.Id;
        PlayerPrefs.SetString(SelectedActionKey, _selectedActionId);
        PlayerPrefs.Save();
        _status = item.Name + " selected. Action package registration will use this package when installed.";
        return true;
    }

    public string GetUserWorkshopFolder()
    {
        return Path.Combine(Application.persistentDataPath, persistentWorkshopFolderName);
    }

    public void OpenUserWorkshopFolder()
    {
        string folder = GetUserWorkshopFolder();
        Directory.CreateDirectory(folder);
        Application.OpenURL("file:///" + NormalizePath(folder).Replace('\\', '/'));
    }

    public bool IsSelected(WorkshopItem item)
    {
        if (item == null)
        {
            return false;
        }

        switch (item.Type)
        {
            case WorkshopItemType.Model:
                return string.Equals(item.Id, _selectedModelId, StringComparison.OrdinalIgnoreCase);
            case WorkshopItemType.Scene:
                return string.Equals(item.Id, _selectedSceneId, StringComparison.OrdinalIgnoreCase);
            case WorkshopItemType.Action:
                return string.Equals(item.Id, _selectedActionId, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private void ResolveReferences()
    {
        if (runtimeControls == null)
        {
            runtimeControls = GetComponentInChildren<TransparentPetRuntimeControls>(true);
        }

        if (actionController == null)
        {
            actionController = GetComponentInChildren<TransparentPetKawaiiActionController>(true);
        }

        if (headLookAt == null)
        {
            headLookAt = GetComponentInChildren<TransparentPetHeadLookAt>(true);
        }

        if (skeletonHitMask == null)
        {
            skeletonHitMask = GetComponentInChildren<TransparentPetSkeletonHitMask>(true);
        }

        if (expressionController == null)
        {
            expressionController = GetComponentInChildren<PetExpressionController>(true);
        }

        if (blinkController == null)
        {
            blinkController = GetComponentInChildren<PetBlinkController>(true);
        }

        if (windowController == null)
        {
            windowController = GetComponentInParent<TransparentWindowController>();
        }

        if (targetCamera == null)
        {
            targetCamera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
        }

        if (modelRoot == null && runtimeControls != null)
        {
            modelRoot = runtimeControls.modelRoot != null ? runtimeControls.modelRoot : runtimeControls.transform;
        }

        if (modelRoot == null && actionController != null)
        {
            modelRoot = actionController.modelRoot != null ? actionController.modelRoot : actionController.transform;
        }
    }

    private void LoadSelectedIds()
    {
        _selectedModelId = PlayerPrefs.GetString(SelectedModelKey, string.Empty);
        _selectedSceneId = PlayerPrefs.GetString(SelectedSceneKey, string.Empty);
        _selectedActionId = PlayerPrefs.GetString(SelectedActionKey, string.Empty);
    }

    private string[] BuildScanRoots()
    {
        List<string> roots = new List<string>
        {
            GetUserWorkshopFolder(),
            Path.Combine(Application.streamingAssetsPath, streamingWorkshopFolderName)
        };

        AddSteamWorkshopRoots(roots);

        if (extraWorkshopRoots != null)
        {
            for (int i = 0; i < extraWorkshopRoots.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(extraWorkshopRoots[i]))
                {
                    roots.Add(extraWorkshopRoots[i]);
                }
            }
        }

        return roots.ToArray();
    }

    private void AddSteamWorkshopRoots(List<string> roots)
    {
        if (string.IsNullOrWhiteSpace(steamAppId))
        {
            return;
        }

        string steamRoot = Environment.GetEnvironmentVariable("STEAM_COMPAT_CLIENT_INSTALL_PATH");
        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            steamRoot = Environment.GetEnvironmentVariable("STEAM_DIR");
        }

        if (string.IsNullOrWhiteSpace(steamRoot))
        {
            return;
        }

        roots.Add(Path.Combine(steamRoot, "steamapps", "workshop", "content", steamAppId));
    }

    private void ScanRoot(string root)
    {
        string directManifest = Path.Combine(root, "manifest.json");
        if (File.Exists(directManifest))
        {
            AddManifest(directManifest);
        }

        string[] manifests;
        try
        {
            manifests = Directory.GetFiles(root, "manifest.json", SearchOption.AllDirectories);
        }
        catch (Exception exc) when (exc is IOException || exc is UnauthorizedAccessException)
        {
            Debug.LogWarning("Could not scan Workshop root: " + root + " (" + exc.Message + ")");
            return;
        }

        Array.Sort(manifests, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < manifests.Length; i++)
        {
            if (!string.Equals(manifests[i], directManifest, StringComparison.OrdinalIgnoreCase))
            {
                AddManifest(manifests[i]);
            }
        }
    }

    private void AddManifest(string manifestPath)
    {
        WorkshopItem item = LoadManifest(manifestPath);
        if (item == null)
        {
            return;
        }

        if (FindItem(_items, item.Id) != null)
        {
            item.Id = item.Id + "#" + _items.Count;
        }

        _items.Add(item);
        switch (item.Type)
        {
            case WorkshopItemType.Model:
                _models.Add(item);
                break;
            case WorkshopItemType.Scene:
                _scenes.Add(item);
                break;
            case WorkshopItemType.Action:
                _actions.Add(item);
                break;
        }
    }

    private WorkshopItem LoadManifest(string manifestPath)
    {
        Dictionary<string, object> data;
        try
        {
            data = TransparentPetJson.AsObject(TransparentPetJson.Parse(File.ReadAllText(manifestPath)));
        }
        catch (Exception exc) when (exc is IOException || exc is ArgumentException || exc is FormatException)
        {
            Debug.LogWarning("Invalid Workshop manifest: " + manifestPath + " (" + exc.Message + ")");
            return null;
        }

        if (data == null)
        {
            return null;
        }

        string rootPath = Path.GetDirectoryName(manifestPath) ?? string.Empty;
        string entry = TransparentPetJson.GetString(data, "entry", string.Empty).Trim();
        string entryPath = ResolvePackagePath(rootPath, entry);
        WorkshopItemType type = ParseType(TransparentPetJson.GetString(data, "type", string.Empty));
        string format = TransparentPetJson.GetString(data, "format", string.Empty);
        if (string.IsNullOrWhiteSpace(format))
        {
            format = Path.GetExtension(entryPath).TrimStart('.').ToLowerInvariant();
        }

        WorkshopItem item = new WorkshopItem
        {
            Id = BuildItemId(rootPath, data),
            Name = TransparentPetJson.GetString(data, "name", Path.GetFileName(rootPath)),
            Type = type,
            RootPath = rootPath,
            ManifestPath = manifestPath,
            Entry = entry,
            EntryPath = entryPath,
            Thumbnail = TransparentPetJson.GetString(data, "thumbnail", string.Empty),
            BundleAssetName = TransparentPetJson.GetString(data, "asset", string.Empty),
            Format = format
        };
        item.ThumbnailPath = ResolvePackagePath(rootPath, item.Thumbnail);
        item.CanApply = CanApply(item, out string status);
        item.Status = status;
        return item;
    }

    private static string BuildItemId(string rootPath, Dictionary<string, object> data)
    {
        string id = TransparentPetJson.GetString(data, "id", string.Empty);
        if (!string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return NormalizePath(rootPath).ToLowerInvariant();
    }

    private static WorkshopItemType ParseType(string value)
    {
        string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "model":
            case "character":
            case "avatar":
                return WorkshopItemType.Model;
            case "scene":
            case "environment":
            case "room":
                return WorkshopItemType.Scene;
            case "action":
            case "animation":
            case "motion":
                return WorkshopItemType.Action;
            default:
                return WorkshopItemType.Unknown;
        }
    }

    private static bool CanApply(WorkshopItem item, out string status)
    {
        if (item.Type == WorkshopItemType.Unknown)
        {
            status = "Unknown item type";
            return false;
        }

        if (string.IsNullOrWhiteSpace(item.Entry))
        {
            status = "Missing entry";
            return false;
        }

        if (!File.Exists(item.EntryPath) && !Directory.Exists(item.EntryPath))
        {
            status = "Entry file is missing";
            return false;
        }

        string extension = Path.GetExtension(item.EntryPath).ToLowerInvariant();
        if (item.Type == WorkshopItemType.Model && (extension == ".assetbundle" || extension == ".bundle"))
        {
            status = "Ready";
            return true;
        }

        if (item.Type == WorkshopItemType.Model && (extension == ".glb" || extension == ".gltf" || extension == ".vrm"))
        {
            status = "Selected only; install a runtime glTF/VRM importer to hot-load this format";
            return false;
        }

        if (extension == ".fbx")
        {
            status = "FBX is creator-source input; publish a converted runtime package for users";
            return false;
        }

        status = "Selected only; runtime loader for this item type is not installed";
        return false;
    }

    private bool TryApplyModelAssetBundle(WorkshopItem item)
    {
        if (modelRoot == null)
        {
            _status = "No model root is bound";
            return false;
        }

        AssetBundle bundle = AssetBundle.LoadFromFile(item.EntryPath);
        if (bundle == null)
        {
            return false;
        }

        try
        {
            string assetName = item.BundleAssetName;
            if (string.IsNullOrWhiteSpace(assetName))
            {
                string[] assetNames = bundle.GetAllAssetNames();
                for (int i = 0; i < assetNames.Length; i++)
                {
                    GameObject candidate = bundle.LoadAsset<GameObject>(assetNames[i]);
                    if (candidate != null)
                    {
                        assetName = assetNames[i];
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(assetName))
            {
                return false;
            }

            GameObject prefab = bundle.LoadAsset<GameObject>(assetName);
            if (prefab == null)
            {
                return false;
            }

            ReplaceModel(prefab);
            return true;
        }
        finally
        {
            bundle.Unload(false);
        }
    }

    private void ReplaceModel(GameObject prefab)
    {
        HideOriginalModel();
        Transform parent = modelRoot != null && modelRoot.parent != null ? modelRoot.parent : transform;
        Vector3 localPosition = modelRoot != null ? modelRoot.localPosition : Vector3.zero;
        Quaternion localRotation = modelRoot != null ? modelRoot.localRotation : Quaternion.identity;
        Vector3 localScale = modelRoot != null ? modelRoot.localScale : Vector3.one;

        if (_loadedModelInstance != null)
        {
            Destroy(_loadedModelInstance);
        }

        _loadedModelInstance = Instantiate(prefab, parent);
        _loadedModelInstance.name = prefab.name + "_Workshop";
        _loadedModelInstance.transform.localPosition = localPosition;
        _loadedModelInstance.transform.localRotation = localRotation;
        _loadedModelInstance.transform.localScale = localScale;

        Animator animator = _loadedModelInstance.GetComponentInChildren<Animator>(true);
        if (animator != null)
        {
            animator.applyRootMotion = false;
        }

        Transform newRoot = _loadedModelInstance.transform;
        EnsureHitCollider(_loadedModelInstance);
        modelRoot = newRoot;
        if (runtimeControls != null)
        {
            runtimeControls.modelRoot = newRoot;
            runtimeControls.ApplyConfiguredDefaults();
        }

        if (actionController != null)
        {
            actionController.RebindModelRoot(newRoot);
        }

        if (expressionController != null)
        {
            expressionController.RebindScanRoot(newRoot);
        }

        if (blinkController != null)
        {
            blinkController.expressionController = expressionController;
            blinkController.Rebind(newRoot);
        }

        if (headLookAt != null)
        {
            headLookAt.Rebind(animator, newRoot, targetCamera);
        }

        if (skeletonHitMask != null)
        {
            skeletonHitMask.animator = animator;
            skeletonHitMask.targetCamera = targetCamera != null ? targetCamera : skeletonHitMask.targetCamera;
        }

        if (windowController != null)
        {
            windowController.hitRoot = newRoot;
            windowController.skeletonHitMask = skeletonHitMask;
        }
    }

    private void HideOriginalModel()
    {
        if (_loadedModelInstance != null || modelRoot == null)
        {
            return;
        }

        Renderer[] renderers = modelRoot.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            renderers[i].enabled = false;
        }

        Collider[] colliders = modelRoot.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            colliders[i].enabled = false;
        }
    }

    private static void EnsureHitCollider(GameObject model)
    {
        if (model == null || model.GetComponentInChildren<Collider>(true) != null)
        {
            return;
        }

        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0)
        {
            return;
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        BoxCollider collider = model.AddComponent<BoxCollider>();
        collider.center = model.transform.InverseTransformPoint(bounds.center);
        Vector3 localSize = model.transform.InverseTransformVector(bounds.size);
        collider.size = new Vector3(
            Mathf.Max(Mathf.Abs(localSize.x), 0.6f),
            Mathf.Max(Mathf.Abs(localSize.y), 1.6f),
            Mathf.Max(Mathf.Abs(localSize.z), 0.35f));
    }

    private static WorkshopItem FindItem(List<WorkshopItem> items, string itemId)
    {
        if (items == null || string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Id, itemId, StringComparison.OrdinalIgnoreCase))
            {
                return items[i];
            }
        }

        return null;
    }

    private static string ResolvePackagePath(string rootPath, string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
        {
            return string.Empty;
        }

        string normalized = relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized))
        {
            return NormalizePath(normalized);
        }

        return NormalizePath(Path.Combine(rootPath, normalized));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }
}
