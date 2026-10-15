using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace CargoHunters.InventoryMod;

/// <summary>
/// Инъекция событий в Unity Input System, без OS SendInput и прямых вызовов обработчиков UI.
/// Нажатие и отпускание разнесены по кадрам, чтобы action bindings увидели переход.
/// </summary>
internal static class KeyboardCommands
{
    private static readonly Dictionary<Key, int> Releases = new();
    private static string? lastKey;
    private static int lastQueuedFrame;

    public static object State()
    {
        var keyboard = Keyboard.current;
        return new
        {
            backend = "Unity Input System", keyboardAvailable = keyboard != null,
            pressed = keyboard == null ? Array.Empty<string>() : Enumerable.Range(0, keyboard.allKeys.Count).Select(i => keyboard.allKeys[i]).Where(x => x.isPressed).Select(x => x.keyCode.ToString()).ToArray(),
            scheduledReleases = Releases.Keys.Select(x => x.ToString()).ToArray(), lastKey, lastQueuedFrame, frame = Time.frameCount
        };
    }

    public static object Press(string name, int frames)
    {
        if (!Enum.TryParse<Key>(name, true, out var key) || !Enum.IsDefined(typeof(Key), key) || key == Key.None)
            throw new ArgumentException("Unknown Input System Key: " + name);
        if (frames < 2 || frames > 60) throw new ArgumentException("frames must be between 2 and 60");
        if (Releases.Count != 0) throw new ArgumentException("A synthetic key is still held; wait for release before the next request");
        var keyboard = Keyboard.current ?? throw new ArgumentException("No current Unity Input System keyboard");
        if (keyboard[key].isPressed) throw new ArgumentException("Key is already pressed");
        QueueKey(keyboard, key, true);
        Releases[key] = Time.frameCount + frames;
        lastKey = key.ToString();
        lastQueuedFrame = Time.frameCount;
        return new { queued = lastKey, queuedFrame = lastQueuedFrame, releaseAfterFrames = frames, backend = "Unity Input System" };
    }

    public static void Update()
    {
        if (Releases.Count == 0) return;
        var due = Releases.Where(x => Time.frameCount >= x.Value).Select(x => x.Key).ToArray();
        if (due.Length == 0 || Keyboard.current == null) return;
        var keyboard = Keyboard.current;
        foreach (var key in due) QueueKey(keyboard, key, false);
        foreach (var key in due) Releases.Remove(key);
    }

    private static unsafe void QueueKey(Keyboard keyboard, Key key, bool pressed)
    {
        // QueueStateEvent<KeyboardState> has no AOT specialization in this game.
        // Use the non-generic event API. This pointer addresses our temporary input
        // event buffer allocated by Unity, not scanned addresses in game memory.
        var buffer = StateEvent.From(keyboard, out var eventPointer, Unity.Collections.Allocator.Temp);
        try
        {
            var state = InputControlExtensions.GetStatePtrFromStateEvent(keyboard[key], eventPointer);
            if (state == null) throw new InvalidOperationException("No keyboard state in event buffer");
            keyboard[key].WriteValueIntoState(pressed ? 1f : 0f, state);
            InputSystem.QueueEvent(eventPointer);
        }
        finally { buffer.Dispose(); }
    }
}
