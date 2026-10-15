using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CargoHunters.InventoryMod;

/// <summary>
/// Снимки uGUI и TextMeshPro. Геометрия и raycast проверяются в Unity, без доступа к окну ОС.
/// </summary>
internal static class UiCommands
{
    internal sealed class Node
    {
        public int id { get; set; }
        public string name { get; set; } = "";
        public string path { get; set; } = "";
        public string kind { get; set; } = "";
        public string? text { get; set; }
        public bool active { get; set; }
        public bool visibleEstimate { get; set; }
        public float alpha { get; set; }
        public bool? interactable { get; set; }
        public object? rect { get; set; }
        public string[] components { get; set; } = Array.Empty<string>();
        public bool childrenTruncated { get; set; }
        public List<Node> children { get; set; } = new();
    }

    public static object Tree(int rootId, int depth, int limit, bool includeInactive)
    {
        Canvas.ForceUpdateCanvases();
        var roots = rootId != 0
            ? new[] { SceneCommands.Find(rootId).TryCast<GameObject>() ?? throw new ArgumentException("rootId must be a GameObject") }
            : Resources.FindObjectsOfTypeAll<Canvas>()
                .Where(x => x != null && x.gameObject.scene.IsValid() && x.GetComponentInParent<Canvas>() == x)
                .Where(x => !HasParentCanvas(x.transform.parent)).Select(x => x.gameObject).ToArray();
        var count = 0;
        var truncated = false;
        Node? Visit(Transform transform, int level)
        {
            var go = transform.gameObject;
            if (!includeInactive && !go.activeInHierarchy) return null;
            if (count >= limit) { truncated = true; return null; }
            count++;
            var selectable = go.GetComponent<Selectable>();
            var text = go.GetComponent<TMP_Text>();
            var legacyText = go.GetComponent<Text>();
            var alpha = EffectiveAlpha(transform);
            var bounds = Bounds(go);
            var node = new Node
            {
                id = SceneCommands.Remember(go), name = go.name, path = SceneCommands.ObjectPath(transform),
                active = go.activeInHierarchy, alpha = alpha,
                visibleEstimate = go.activeInHierarchy && alpha > 0.001f && bounds.OnScreen && CanvasEnabled(go),
                kind = Kind(go), text = Trim(text != null ? text.text : legacyText != null ? legacyText.text : null),
                interactable = selectable != null ? selectable.IsActive() && selectable.IsInteractable() : null,
                rect = bounds.Rect,
                components = go.GetComponents<Component>().Where(x => x != null).Select(x => x.GetIl2CppType().FullName).ToArray()
            };
            if (level < depth)
            {
                for (var i = 0; i < transform.childCount; i++)
                {
                    var child = Visit(transform.GetChild(i), level + 1);
                    if (child != null) node.children.Add(child);
                    if (count >= limit) { node.childrenTruncated = i + 1 < transform.childCount; break; }
                }
            }
            else node.childrenTruncated = transform.childCount > 0;
            return node;
        }
        var output = new List<Node>();
        foreach (var root in roots)
        {
            var node = Visit(root.transform, 0);
            if (node != null) output.Add(node);
            if (count >= limit) { truncated = true; break; }
        }
        return new
        {
            screen = new { width = Screen.width, height = Screen.height, coordinates = "Unity screen pixels, origin bottom-left" },
            roots = output, count, truncated, depth,
            note = "visibleEstimate checks activity, canvas, alpha and screen bounds; occlusion is checked separately by /ui/click"
        };
    }

    private static bool HasParentCanvas(Transform? transform)
    {
        for (var parent = transform; parent != null; parent = parent.parent)
            if (parent.GetComponent<Canvas>() != null) return true;
        return false;
    }

    private static bool CanvasEnabled(GameObject go)
    {
        var canvas = go.GetComponentInParent<Canvas>();
        return canvas == null || canvas.enabled;
    }

    private static float EffectiveAlpha(Transform transform)
    {
        var alpha = 1f;
        for (var current = transform; current != null; current = current.parent)
        {
            foreach (var group in current.GetComponents<CanvasGroup>())
                if (group != null && group.enabled) alpha *= group.alpha;
        }
        return alpha;
    }

    private static string? Trim(string? text) => text is { Length: > 1500 } ? text[..1500] + "..." : text;

    private static string Kind(GameObject go)
    {
        if (go.GetComponent<Button>() != null) return "Button";
        if (go.GetComponent<Toggle>() != null) return "Toggle";
        if (go.GetComponent<TMP_InputField>() != null || go.GetComponent<InputField>() != null) return "InputField";
        if (go.GetComponent<TMP_Dropdown>() != null || go.GetComponent<Dropdown>() != null) return "Dropdown";
        if (go.GetComponent<Slider>() != null) return "Slider";
        if (go.GetComponent<ScrollRect>() != null) return "ScrollRect";
        if (go.GetComponent<TMP_Text>() != null || go.GetComponent<Text>() != null) return "Text";
        if (go.GetComponent<Canvas>() != null) return "Canvas";
        if (go.GetComponent<Image>() != null) return "Image";
        return "Container";
    }

    private static (object? Rect, Vector2 Center, bool OnScreen) Bounds(GameObject go)
    {
        var rect = go.GetComponent<RectTransform>();
        if (rect == null) return (null, Vector2.zero, false);
        var canvas = go.GetComponentInParent<Canvas>();
        var camera = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var corners = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<Vector3>(4);
        rect.GetWorldCorners(corners);
        var points = Enumerable.Range(0, 4).Select(i => RectTransformUtility.WorldToScreenPoint(camera, corners[i])).ToArray();
        var x = points.Min(p => p.x); var y = points.Min(p => p.y);
        var width = points.Max(p => p.x) - x; var height = points.Max(p => p.y) - y;
        var center = new Vector2(x + width / 2, y + height / 2);
        return (new { x, y, width, height, centerX = center.x, centerY = center.y }, center,
            width > 0 && height > 0 && x < Screen.width && y < Screen.height && x + width > 0 && y + height > 0);
    }

    public static object Click(int id)
    {
        var go = SceneCommands.Find(id).TryCast<GameObject>() ?? throw new ArgumentException("Click ID must refer to a GameObject from /ui/tree");
        if (!go.activeInHierarchy || EffectiveAlpha(go.transform) <= 0.001f || !CanvasEnabled(go)) throw new ArgumentException("UI element is inactive or transparent");
        var selectable = go.GetComponent<Selectable>();
        if (selectable != null && (!selectable.IsActive() || !selectable.IsInteractable())) throw new ArgumentException("UI element is not interactable");
        Canvas.ForceUpdateCanvases();
        var bounds = Bounds(go);
        if (!bounds.OnScreen) throw new ArgumentException("UI element has no on-screen rectangle");
        var eventSystem = EventSystem.current;
        if (eventSystem == null) throw new ArgumentException("No active EventSystem");
        var pointer = new PointerEventData(eventSystem)
        {
            position = bounds.Center, pressPosition = bounds.Center,
            button = PointerEventData.InputButton.Left, clickCount = 1, eligibleForClick = true
        };
        var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
        eventSystem.RaycastAll(pointer, results);
        if (results.Count == 0) throw new ArgumentException("No raycast target at element center");
        var hit = results[0];
        if (hit.gameObject == null || (hit.gameObject != go && !hit.gameObject.transform.IsChildOf(go.transform)))
            throw new ArgumentException("Element center is blocked by " + (hit.gameObject != null ? SceneCommands.ObjectPath(hit.gameObject.transform) : "unknown object"));
        pointer.pointerCurrentRaycast = hit;
        pointer.pointerPressRaycast = hit;
        var handler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(hit.gameObject);
        if (handler == null || (handler != go && !handler.transform.IsChildOf(go.transform))) throw new ArgumentException("Selected object has no matching click handler");
        var path = SceneCommands.ObjectPath(handler.transform);
        // Dispatch the normal pointer sequence instead of bypassing the UI with onClick.Invoke.
        ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerEnterHandler);
        var pressed = ExecuteEvents.ExecuteHierarchy(hit.gameObject, pointer, ExecuteEvents.pointerDownHandler);
        pointer.pointerPress = pressed != null ? pressed : handler;
        pointer.rawPointerPress = hit.gameObject;
        ExecuteEvents.Execute(pointer.pointerPress, pointer, ExecuteEvents.pointerUpHandler);
        ExecuteEvents.Execute(handler, pointer, ExecuteEvents.pointerClickHandler);
        return new { clicked = id, handler = path, x = bounds.Center.x, y = bounds.Center.y, note = "Pointer events dispatched; refresh /ui/tree to verify the resulting screen" };
    }
}
