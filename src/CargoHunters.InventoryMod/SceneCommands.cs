using System.Globalization;
using System.Reflection;
using System.Text.Json;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace CargoHunters.InventoryMod;

/// <summary>
/// Ограниченные снимки Unity и явные вызовы методов. Вызывать только из главного потока.
/// ID действуют только в текущем процессе и могут устаревать после смены сцены.
/// </summary>
internal static class SceneCommands
{
    private static readonly Dictionary<int, UObject> Handles = new();
    private static readonly Dictionary<string, object> References = new();
    private static int referenceSequence;
    private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

    public static object Execute(string method, string target, string body)
    {
        var uri = new Uri("http://localhost" + target);
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2)).ToDictionary(x => Uri.UnescapeDataString(x[0]), x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
        string Get(string key, string fallback = "") => query.TryGetValue(key, out var value) ? value : fallback;
        int Number(string key, int fallback) => int.Parse(Get(key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        object Target() => Get("referenceId") is { Length: > 0 } reference ? FindReference(reference) : Find(Number("id", 0));

        if (method == "GET")
        {
            switch (uri.AbsolutePath)
            {
                case "/scenes": return Scenes();
                case "/input/state": return KeyboardCommands.State();
                case "/ui/tree": return UiCommands.Tree(Number("rootId", 0), Math.Clamp(Number("depth", 12), 0, 40), Math.Clamp(Number("limit", 1500), 1, 3000), Get("includeInactive", "false") == "true");
                case "/objects": return Objects(Get("name"), Math.Clamp(Number("limit", 100), 1, 300), Math.Clamp(Number("depth", 5), 0, 20), Number("rootId", 0));
                case "/object": return Describe(Find(Number("id", 0)));
                case "/component": return Inspect(Target());
                case "/read": return Read(Target(), Get("member"));
                case "/scripts": return new { scripts = new[] { "scene-summary", "quit" }, note = "POST /scripts/run with {name}; arbitrary C# compilation is not implemented" };
                case "/inventory/settings": return InventoryCommands.GetSettings();
            }
        }
        if (method == "POST" && uri.AbsolutePath == "/input/key")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return KeyboardCommands.Press(root.GetProperty("key").GetString() ?? "", root.TryGetProperty("frames", out var frames) ? frames.GetInt32() : 3);
        }
        if (method == "POST" && uri.AbsolutePath == "/inventory/export")
        {
            using var document = JsonDocument.Parse(body);
            return InventoryCommands.Export(document.RootElement);
        }
        if (method == "POST" && uri.AbsolutePath == "/inventory/settings")
        {
            using var document = JsonDocument.Parse(body);
            return InventoryCommands.SetSettings(document.RootElement);
        }
        if (method == "POST" && uri.AbsolutePath == "/ui/click")
        {
            using var document = JsonDocument.Parse(body);
            return UiCommands.Click(document.RootElement.GetProperty("id").GetInt32());
        }
        if (method == "POST" && uri.AbsolutePath == "/invoke")
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var invokeTarget = root.TryGetProperty("referenceId", out var referenceId) ? FindReference(referenceId.GetString() ?? "") : Find(root.GetProperty("id").GetInt32());
            return Invoke(invokeTarget, root.GetProperty("method").GetString() ?? "",
                root.TryGetProperty("arguments", out var args) ? args.EnumerateArray().ToArray() : Array.Empty<JsonElement>());
        }
        if (method == "POST" && uri.AbsolutePath == "/scripts/run")
        {
            using var document = JsonDocument.Parse(body);
            var name = document.RootElement.GetProperty("name").GetString();
            if (name == "quit") { Application.Quit(); return new { quitRequested = true }; }
            if (name != "scene-summary") throw new ArgumentException("Unknown script");
            return new { script = name, scenes = Scenes(), objects = Objects("", 30, 0) };
        }
        throw new ArgumentException("Unknown route or HTTP method");
    }

    private static object Scenes()
    {
        var active = SceneManager.GetActiveScene();
        var scenes = new List<object>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            scenes.Add(new { scene.name, scene.path, scene.buildIndex, scene.isLoaded, scene.rootCount, active = scene.handle == active.handle });
        }
        return new { active = active.name, scenes, note = "Persistent DontDestroyOnLoad objects are included in /objects" };
    }

    internal static int Remember(UObject obj)
    {
        // Wrappers are a lookup cache, not an ownership mechanism. Clear an old large cache.
        if (Handles.Count > 10000) Handles.Clear();
        var id = obj.GetInstanceID();
        Handles[id] = obj;
        return id;
    }

    internal static UObject Find(int id)
    {
        if (Handles.TryGetValue(id, out var obj) && obj != null) return obj;
        throw new ArgumentException("Unknown or destroyed object ID; refresh /objects and /object first");
    }

    private static object FindReference(string id)
    {
        if (References.TryGetValue(id, out var value)) return value;
        throw new ArgumentException("Unknown referenceId; read the parent member again");
    }

    private static object Objects(string name, int limit, int depth, int rootId = 0)
    {
        var output = new List<object>();
        var roots = rootId != 0
            ? new[] { Find(rootId).TryCast<GameObject>() ?? throw new ArgumentException("rootId must refer to a GameObject") }
            : Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(x => x != null && x.scene.IsValid() && x.transform.parent == null).ToArray();
        var queue = new Queue<(Transform Node, int Depth)>();
        foreach (var root in roots) queue.Enqueue((root.transform, 0));
        var visited = 0;
        while (queue.Count > 0 && output.Count < limit && visited < 5000)
        {
            var (node, level) = queue.Dequeue();
            visited++;
            if (node.name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                var go = node.gameObject;
                output.Add(new { id = Remember(go), go.name, path = ObjectPath(node), scene = go.scene.name, go.activeSelf, go.activeInHierarchy, childCount = node.childCount, depth = level });
            }
            if (level < depth)
                for (var i = 0; i < node.childCount; i++) queue.Enqueue((node.GetChild(i), level + 1));
        }
        return new { objects = output, visited, truncated = queue.Count > 0, limit, depth };
    }

    internal static string ObjectPath(Transform transform)
    {
        var parts = new List<string>();
        for (var current = transform; current != null && parts.Count < 40; current = current.parent) parts.Add(current.name);
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static object Describe(UObject obj)
    {
        var go = obj.TryCast<GameObject>();
        if (go == null) throw new ArgumentException("ID must refer to a GameObject");
        return new
        {
            id = Remember(go), go.name, path = ObjectPath(go.transform), scene = go.scene.name, go.activeSelf, go.activeInHierarchy,
            components = go.GetComponents<Component>().Where(x => x != null).Take(100)
                .Select(x => new { id = Remember(x), type = x.GetIl2CppType().FullName }).ToArray(),
            children = Enumerable.Range(0, Math.Min(go.transform.childCount, 300)).Select(i =>
            {
                var child = go.transform.GetChild(i).gameObject;
                return new { id = Remember(child), child.name, child.activeSelf };
            }).ToArray()
        };
    }

    // GetComponents<Component>() yields base wrappers; resolve the actual generated type
    // so reflection can see the game's members instead of only UnityEngine.Component.
    private static object Typed(object obj)
    {
        if (obj is not Il2CppSystem.Object ilObject) return obj;
        var fullName = ilObject.GetIl2CppType().FullName;
        var type = AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType(fullName, false)).FirstOrDefault(x => x != null);
        // Closed generic wrappers may not resolve through Assembly.GetType; their
        // existing managed wrapper still exposes its declared generated members.
        if (type == null) return obj;
        if (type.IsInstanceOfType(obj)) return obj;
        return Activator.CreateInstance(type, ilObject.Pointer) ?? throw new ArgumentException("Cannot create interop wrapper");
    }

    private static object Inspect(object obj)
    {
        var typed = Typed(obj);
        var type = typed.GetType();
        return new
        {
            id = obj is UObject unityObject ? (int?)Remember(unityObject) : null, type = type.FullName,
            fields = type.GetFields(PublicInstance).Take(150).Select(x => new { x.Name, type = x.FieldType.FullName, value = FieldValue(x, typed) }).ToArray(),
            // Getters can perform work or mutate state; do not evaluate them in a bulk dump.
            properties = type.GetProperties(PublicInstance).Take(150).Select(x => new { x.Name, type = x.PropertyType.FullName, readable = x.CanRead, writable = x.CanWrite }).ToArray(),
            methods = type.GetMethods(PublicInstance).Where(x => !x.IsSpecialName).Take(200).Select(x => new
            {
                x.Name, returns = x.ReturnType.FullName,
                parameters = x.GetParameters().Select(p => new { p.Name, type = p.ParameterType.FullName }).ToArray()
            }).ToArray()
        };
    }

    private static object? FieldValue(FieldInfo field, object target)
    {
        try { return Value(field.GetValue(target)); }
        catch (Exception error) { return new { error = error.GetBaseException().Message }; }
    }

    private static object Read(object obj, string member)
    {
        var target = Typed(obj);
        var type = target.GetType();
        var field = type.GetField(member, PublicInstance);
        if (field != null) return new { member, value = Value(field.GetValue(target)) };
        var property = type.GetProperty(member, PublicInstance);
        if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0) throw new ArgumentException("Readable public member not found");
        return new { member, value = Value(property.GetValue(target)) };
    }

    private static object Invoke(object obj, string name, JsonElement[] arguments)
    {
        var target = Typed(obj);
        var candidates = target.GetType().GetMethods(PublicInstance)
            .Where(x => x.Name == name && !x.IsGenericMethod && !x.IsSpecialName && x.GetParameters().Length == arguments.Length).ToArray();
        var callable = new List<(MethodInfo Method, object?[] Arguments)>();
        foreach (var candidate in candidates)
        {
            try { callable.Add((candidate, candidate.GetParameters().Select((p, i) => ConvertArgument(arguments[i], p.ParameterType)).ToArray())); }
            catch (ArgumentException) { }
        }
        if (callable.Count != 1) throw new ArgumentException("Expected one unambiguous method with supported arguments; found " + callable.Count);
        var call = callable[0];
        return new { invoked = name, value = Value(call.Method.Invoke(target, call.Arguments)) };
    }

    private static object? ConvertArgument(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Null && (!type.IsValueType || Nullable.GetUnderlyingType(type) != null)) return null;
        if (type == typeof(string)) return value.GetString();
        if (type == typeof(Il2CppSystem.String)) return (Il2CppSystem.String)(value.GetString() ?? "");
        if (type == typeof(bool)) return value.GetBoolean();
        if (type.IsEnum) return Enum.Parse(type, value.ToString());
        if (type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr))
            return Convert.ChangeType(value.ToString(), type, CultureInfo.InvariantCulture);
        if (typeof(UObject).IsAssignableFrom(type) && value.TryGetProperty("objectId", out var id))
        {
            var obj = Typed(Find(id.GetInt32()));
            if (type.IsInstanceOfType(obj)) return obj;
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("referenceId", out var referenceId))
        {
            var obj = Typed(FindReference(referenceId.GetString() ?? ""));
            if (type.IsInstanceOfType(obj)) return obj;
        }
        throw new ArgumentException("Unsupported argument type: " + type.FullName);
    }

    private static object? Value(object? value)
    {
        if (value == null) return null;
        var type = value.GetType();
        if (value is string text) return text.Length > 2000 ? text[..2000] + "..." : text;
        if (value is Il2CppSystem.String ilText) return Value((string)ilText);
        if (value is IntPtr pointer) return new { pointer = pointer.ToInt64() };
        if (value is UIntPtr unsignedPointer) return new { pointer = unsignedPointer.ToUInt64() };
        if (value is Vector3 vector) return new { vector.x, vector.y, vector.z };
        if (value is Vector2 vector2) return new { vector2.x, vector2.y };
        if (value is Quaternion rotation) return new { rotation.x, rotation.y, rotation.z, rotation.w };
        if (value is Color color) return new { color.r, color.g, color.b, color.a };
        if (type.IsPrimitive || value is decimal) return value;
        if (type.IsEnum) return value.ToString();
        if (value is UObject obj && obj != null) return new { objectId = Remember(obj), obj.name, type = obj.GetIl2CppType().FullName };
        // Reference handles allow explicit navigation without recursively serializing game state.
        // Their lifetime is bounded; a stale handle can be refreshed by reading its parent again.
        if (References.Count >= 2000) References.Clear();
        var referenceId = "r" + ++referenceSequence;
        References[referenceId] = value;
        return new { referenceId, type = type.FullName };
    }
}
