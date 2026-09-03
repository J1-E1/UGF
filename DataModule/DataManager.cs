using Cysharp.Threading.Tasks;
using Hollow.Core;
using Hollow.Engine.Asset;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Assemblies;

namespace Hollow.Runtime.Data
{
    /// <summary>
    /// Configuration data manager 鈥?loads Luban-generated tables via Addressables.
    ///
    /// Load policy:
    ///   AlwaysLoaded  loaded at InitializeAsync(), stays in memory.
    ///   OnDemand      loaded on first Get / EnsureLoadedAsync / LoadGroupAsync.
    ///
    /// Runtime uses .bytes only; AssetFormat param kept for API compatibility.
    /// </summary>
    public class DataManager : Singleton<DataManager>
    {
        #region Inner Types

        public enum TableLoadPolicy
        { AlwaysLoaded, OnDemand }

        public enum AssetFormat
        { Binary, Json, BinWithJsonFallback }  // kept for API compatibility; runtime uses .bytes only

        private struct TableDescriptor
        {
            public string AssetPath;  // no extension, e.g. "Config/tbchapter"
            public string GroupName;
            public TableLoadPolicy Policy;
            public AssetFormat Format;     // ignored; kept for compatibility
        }

        #endregion Inner Types

        #region Fields

        private readonly Dictionary<Type, Dictionary<int, object>> _dataContainer = new();
        private readonly Dictionary<Type, TableDescriptor> _registry = new();
        private readonly Dictionary<string, List<Type>> _groups = new(StringComparer.Ordinal);
        private readonly HashSet<string> _loadedGroups = new(StringComparer.Ordinal);
        private readonly HashSet<Type> _pendingLoad = new();

        private readonly Dictionary<Type, MethodInfo> _loaderMethodCache = new();
        private readonly Dictionary<Type, MethodInfo> _deserializeCache = new();
        private readonly Dictionary<Type, FieldInfo> _idMemberCache = new();
        private Type _byteBufType;
        private MethodInfo _readIntMethod;

        private FStreamableManager _streamableManager;

        #endregion Fields

        #region Properties

        public bool IsInitialized { get; private set; }

        #endregion Properties

        #region Setup

        public void SetStreamableManager(FStreamableManager streamableManager)
        {
            _streamableManager = streamableManager;
        }

        // Backward-compatible overload 鈥?defaults to BinWithJsonFallback.
        public void RegisterGroup(string groupName, TableLoadPolicy policy,
                                  params (Type type, string assetPath)[] entries)
            => RegisterGroup(groupName, policy, AssetFormat.BinWithJsonFallback, entries);

        public void RegisterGroup(string groupName, TableLoadPolicy policy, AssetFormat format,
                                  params (Type type, string assetPath)[] entries)
        {
            if (!_groups.TryGetValue(groupName, out var typeList))
            {
                typeList = new List<Type>();
                _groups[groupName] = typeList;
            }

            foreach (var (type, assetPath) in entries)
            {
                _registry[type] = new TableDescriptor
                {
                    AssetPath = assetPath,
                    GroupName = groupName,
                    Policy = policy,
                    Format = format,
                };
                if (!typeList.Contains(type))
                    typeList.Add(type);
            }
        }

        public async UniTask InitializeAsync()
        {
            _dataContainer.Clear();
            _loadedGroups.Clear();
            _pendingLoad.Clear();

            var coreGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in _groups)
            {
                foreach (var t in kv.Value)
                {
                    if (_registry.TryGetValue(t, out var d) && d.Policy == TableLoadPolicy.AlwaysLoaded)
                    {
                        coreGroups.Add(kv.Key);
                        break;
                    }
                }
            }

            var initTasks = new List<UniTask>(coreGroups.Count);
            foreach (var g in coreGroups) initTasks.Add(LoadGroupAsync(g));
            await UniTask.WhenAll(initTasks);
            IsInitialized = true;
        }

        #endregion Setup

        #region Group Load / Unload

        public async UniTask LoadGroupAsync(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var types))
            {
                Debug.LogWarning($"[DataManager] Group not registered: {groupName}");
                return;
            }
            var groupTasks = new List<UniTask>(types.Count);
            foreach (var t in types) groupTasks.Add(LoadTableByTypeAsync(t));
            await UniTask.WhenAll(groupTasks);
            _loadedGroups.Add(groupName);
        }

        /// <summary>Unloads OnDemand tables in a group. AlwaysLoaded tables are skipped.</summary>
        public void UnloadGroup(string groupName)
        {
            if (!_groups.TryGetValue(groupName, out var types)) return;

            bool anyRemoved = false;
            foreach (var type in types)
            {
                if (_registry.TryGetValue(type, out var desc) && desc.Policy == TableLoadPolicy.OnDemand)
                {
                    _dataContainer.Remove(type);
                    anyRemoved = true;
                }
            }
            if (anyRemoved) _loadedGroups.Remove(groupName);
        }

        public bool IsGroupLoaded(string groupName) => _loadedGroups.Contains(groupName);

        #endregion Group Load / Unload

        #region Pre-warm

        public UniTask EnsureLoadedAsync<T>() where T : class
            => LoadTableByTypeAsync(typeof(T));

        public UniTask EnsureGroupLoadedAsync(string groupName)
            => _loadedGroups.Contains(groupName) ? UniTask.CompletedTask : LoadGroupAsync(groupName);

        #endregion Pre-warm

        #region Access

        /// <summary>
        /// Returns config by ID, or null if not found.
        /// OnDemand tables trigger background load on first access 鈥?use EnsureLoadedAsync to pre-warm.
        /// </summary>
        public T Get<T>(int id, bool logIfMissing = false) where T : class
        {
            if (id <= 0) return null;

            Type type = typeof(T);
            if (!_dataContainer.TryGetValue(type, out var dict))
            {
                TriggerOnDemandLoad(type, logIfMissing);
                return null;
            }

            if (!dict.TryGetValue(id, out var obj))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (logIfMissing)
                    Debug.LogWarning($"[DataManager] {type.Name} ID:{id} not found");
#endif
                return null;
            }
            return obj as T;
        }

        /// <summary>Returns typed dictionary of all rows. Allocates 鈥?prefer GetList.</summary>
        public Dictionary<int, T> GetAll<T>() where T : class
        {
            Type type = typeof(T);
            if (!_dataContainer.TryGetValue(type, out var dict))
            {
                TriggerOnDemandLoad(type, true);
                return null;
            }
            var result = new Dictionary<int, T>(dict.Count);
            foreach (var kvp in dict) result[kvp.Key] = (T)kvp.Value;
            return result;
        }

        public List<T> GetList<T>() where T : class
        {
            if (!_dataContainer.TryGetValue(typeof(T), out var dict)) return new List<T>();
            var list = new List<T>(dict.Count);
            foreach (var v in dict.Values) list.Add((T)v);
            return list;
        }

        public T GetOrDefault<T>(int id) where T : class
        {
            if (id <= 0) return null;
            if (!_dataContainer.TryGetValue(typeof(T), out var dict)) return null;
            dict.TryGetValue(id, out var obj);
            return obj as T;
        }

        public int GetCount<T>() where T : class
            => _dataContainer.TryGetValue(typeof(T), out var dict) ? dict.Count : 0;

        #endregion Access

        #region Query

        public List<T> FindAll<T>(Func<T, bool> predicate) where T : class
        {
            if (!_dataContainer.TryGetValue(typeof(T), out var dict)) return new List<T>();
            var result = new List<T>();
            foreach (var v in dict.Values)
                if (v is T t && predicate(t)) result.Add(t);
            return result;
        }

        public T Find<T>(Func<T, bool> predicate) where T : class
        {
            if (!_dataContainer.TryGetValue(typeof(T), out var dict)) return null;
            foreach (var v in dict.Values)
                if (v is T t && predicate(t)) return t;
            return null;
        }

        public bool Any<T>(Func<T, bool> predicate) where T : class
        {
            if (!_dataContainer.TryGetValue(typeof(T), out var dict)) return false;
            foreach (var v in dict.Values)
                if (v is T t && predicate(t)) return true;
            return false;
        }

        #endregion Query

        #region Clear

        public void Clear<T>() where T : class
        {
            _dataContainer.Remove(typeof(T));
        }

        public void ClearAll()
        {
            _dataContainer.Clear();
            _loadedGroups.Clear();
            _pendingLoad.Clear();
        }

        #endregion Clear

        #region Direct Load

        /// <summary>
        /// Load table by address (replace mode). No RegisterGroup needed.
        /// assetPath without extension 鈥?DataManager appends .bytes/.json.
        /// </summary>
        public UniTask LoadConfigAsync<T>(string assetPath,
                                          AssetFormat format = AssetFormat.BinWithJsonFallback) where T : class
            => LoadConfigInternalAsync<T>(assetPath, false, format);

        #endregion Direct Load

        #region Internal Load

        private async UniTask LoadTableByTypeAsync(Type type)
        {
            if (_dataContainer.ContainsKey(type)) return;

            if (!_registry.TryGetValue(type, out var desc))
            {
                Debug.LogWarning($"[DataManager] Type {type.Name} not registered");
                return;
            }

            var method = GetOrCacheLoaderMethod(type);
            if (method == null)
            {
                Debug.LogError($"[DataManager] Could not resolve loader for {type.Name}");
                return;
            }

            await (UniTask)method.Invoke(this, new object[] { desc.AssetPath, false, desc.Format });

            if (_dataContainer.ContainsKey(type) && _groups.TryGetValue(desc.GroupName, out var groupTypes))
            {
                bool allLoaded = true;
                foreach (var t in groupTypes)
                    if (!_dataContainer.ContainsKey(t)) { allLoaded = false; break; }
                if (allLoaded) _loadedGroups.Add(desc.GroupName);
            }
        }

        private void TriggerOnDemandLoad(Type type, bool warn)
        {
            if (!_registry.TryGetValue(type, out var desc))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (warn) Debug.LogWarning($"[DataManager] {type.Name} not registered 鈥?call RegisterGroup first");
#endif
                return;
            }

            if (desc.Policy == TableLoadPolicy.AlwaysLoaded)
            {
                Debug.LogError($"[DataManager] Core table {type.Name} not loaded 鈥?was InitializeAsync() called?");
                return;
            }

            if (_pendingLoad.Add(type))
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                Debug.LogWarning($"[DataManager] OnDemand table {type.Name} not loaded 鈥?use EnsureLoadedAsync<{type.Name}>() to pre-warm.");
#endif
                FireAndForget(type, desc.GroupName).Forget();
            }
        }

        private async UniTaskVoid FireAndForget(Type type, string groupName)
        {
            try { await LoadTableByTypeAsync(type); }
            finally
            {
                _pendingLoad.Remove(type);
                if (_groups.TryGetValue(groupName, out var groupTypes))
                {
                    bool allLoaded = true;
                    foreach (var t in groupTypes)
                        if (!_dataContainer.ContainsKey(t)) { allLoaded = false; break; }
                    if (allLoaded) _loadedGroups.Add(groupName);
                }
            }
        }

        private MethodInfo GetOrCacheLoaderMethod(Type type)
        {
            if (_loaderMethodCache.TryGetValue(type, out var cached)) return cached;

            var method = typeof(DataManager)
                .GetMethod(nameof(LoadConfigInternalAsync),
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new[] { typeof(string), typeof(bool), typeof(AssetFormat) },
                    null)
                ?.MakeGenericMethod(type);

            if (method != null) _loaderMethodCache[type] = method;
            return method;
        }

        private const string ExtBinary = ".bytes";

        private async UniTask LoadConfigInternalAsync<T>(string assetPath, bool mergeMode,
                                                         AssetFormat format = AssetFormat.BinWithJsonFallback) where T : class
        {
            if (_streamableManager == null)
            {
                Debug.LogError("[DataManager] StreamableManager not set 鈥?call SetStreamableManager() before loading");
                return;
            }

            // Only .bytes is used at runtime; format param kept for API compatibility
            var asset = await _streamableManager.LoadAssetAsync<TextAsset>(assetPath + ExtBinary);
            if (asset == null)
            {
                Debug.LogError($"[DataManager] Load failed: {assetPath}");
                return;
            }

            if (mergeMode) ParseAndMerge<T>(asset);
            else ParseAndStore<T>(asset);
        }

        #endregion Internal Load

        #region Parse

        private void ParseAndStore<T>(TextAsset asset) where T : class
        {
            try
            {
                var dict = ParseBinaryData<T>(asset.bytes);
                _dataContainer[typeof(T)] = dict;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DataManager] Parse error {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ParseAndMerge<T>(TextAsset asset) where T : class
        {
            try
            {
                var newDict = ParseBinaryData<T>(asset.bytes);
                MergeDict(typeof(T), newDict);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DataManager] Merge error {typeof(T).Name}: {ex.Message}");
            }
        }

        private void MergeDict(Type type, Dictionary<int, object> newDict)
        {
            if (!_dataContainer.ContainsKey(type))
                _dataContainer[type] = new Dictionary<int, object>();
            foreach (var kvp in newDict)
                _dataContainer[type][kvp.Key] = kvp.Value;
        }

        private Dictionary<int, object> ParseBinaryData<T>(byte[] bytes) where T : class
        {
            var result = new Dictionary<int, object>();
            try
            {
                EnsureByteBufType();
                if (_byteBufType == null)
                {
                    Debug.LogError("[DataManager] Luban.ByteBuf not found");
                    return result;
                }

                var deserialize = GetOrCacheDeserializeMethod(typeof(T), _byteBufType);
                if (deserialize == null)
                {
                    Debug.LogError($"[DataManager] No binary Deserialize method for {typeof(T).Name}");
                    return result;
                }

                var buf = Activator.CreateInstance(_byteBufType, bytes);
                int count = (int)_readIntMethod.Invoke(buf, null);

                for (int i = 0; i < count; i++)
                {
                    T obj = deserialize.Invoke(null, new object[] { buf }) as T;
                    if (obj == null) continue;
                    int id = ExtractId(obj);
                    if (id != -1) result[id] = obj;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DataManager] Binary parse error {typeof(T).Name}: {ex.Message}\n{ex.StackTrace}");
            }
            return result;
        }

        private void EnsureByteBufType()
        {
            if (_byteBufType != null) return;
            foreach (var asm in CurrentAssemblies.GetLoadedAssemblies())
            {
                _byteBufType = asm.GetType("Luban.ByteBuf");
                if (_byteBufType != null)
                {
                    _readIntMethod = _byteBufType.GetMethod("ReadInt", BindingFlags.Public | BindingFlags.Instance);
                    break;
                }
            }
        }

        private MethodInfo GetOrCacheDeserializeMethod(Type type, Type paramType = null)
        {
            if (_deserializeCache.TryGetValue(type, out var cached)) return cached;

            MethodInfo method;
            if (paramType != null)
            {
                method = type.GetMethod($"Deserialize{type.Name}",
                    BindingFlags.Public | BindingFlags.Static,
                    null, new[] { paramType }, null);
            }
            else
            {
                method = type.GetMethod($"Deserialize{type.Name}",
                    BindingFlags.Public | BindingFlags.Static);
            }

            if (method != null) _deserializeCache[type] = method;
            return method;
        }

        private int ExtractId(object obj)
        {
            Type type = obj.GetType();

            if (_idMemberCache.TryGetValue(type, out var cachedMember))
            {
                if (cachedMember == null) return -1;
                return (int)cachedMember.GetValue(obj);
            }

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(int) &&
                    field.Name.EndsWith("Id", StringComparison.OrdinalIgnoreCase))
                {
                    _idMemberCache[type] = field;
                    return (int)field.GetValue(obj);
                }
            }

            _idMemberCache[type] = null;
            Debug.LogWarning($"[DataManager] Could not find ID field for {type.Name}");
            return -1;
        }

        #endregion Parse

        #region Lifecycle

        public void Dispose()
        {
            ClearAll();
            _registry.Clear();
            _groups.Clear();
            _deserializeCache.Clear();
            _idMemberCache.Clear();
            _loaderMethodCache.Clear();
            _byteBufType = null;
            _readIntMethod = null;
            IsInitialized = false;
            _streamableManager = null;
            TearDown();
        }

        #endregion Lifecycle
    }
}