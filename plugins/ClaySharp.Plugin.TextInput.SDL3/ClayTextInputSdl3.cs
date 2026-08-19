// Clay Text Input — SDL3 platform adapter.
//
// Wires ClayTextInput up to SDL3 for clipboard access and cursor swapping, and
// forwards SDL3 keyboard / text-input events into ClayTextInput.OnChar / OnKey.
//
// Usage:
//   ClayTextInput.SetPlatform(ClayTextInputSdl3.Platform());
//   SDL.StartTextInput(window);          // enable SDL_EVENT_TEXT_INPUT
//   // in your event loop:
//   ClayTextInputSdl3.ProcessEvent(ref sdlEvent);

using System;
using System.Runtime.InteropServices;
using System.Text;
using ClaySharp.Plugin.TextInput;
using SDL3;

namespace ClaySharp.Plugin.TextInput.SDL3;

public static class ClayTextInputSdl3
{
    // Lazily created I-beam cursor; cached for the process lifetime.
    private static nint _ibeamCursor;

    public static ClayTextInput.Platform Platform()
    {
        return new ClayTextInput.Platform
        {
            GetClipboardText = _ => SDL.GetClipboardText(),
            SetClipboardText = (_, text) => SDL.SetClipboardText(text),
            SetIbeamCursor = _ =>
            {
                if (_ibeamCursor == 0)
                    _ibeamCursor = SDL.CreateSystemCursor(SDL.SystemCursor.Text);
                SDL.SetCursor(_ibeamCursor);
            },
            ResetCursor = _ => SDL.SetCursor(SDL.GetDefaultCursor()),
            UserData = null,
        };
    }

    public static ClayTextInput.Key? MapKey(SDL.Keycode key) => key switch
    {
        SDL.Keycode.Left => ClayTextInput.Key.Left,
        SDL.Keycode.Right => ClayTextInput.Key.Right,
        SDL.Keycode.Home => ClayTextInput.Key.Home,
        SDL.Keycode.End => ClayTextInput.Key.End,
        SDL.Keycode.Backspace => ClayTextInput.Key.Backspace,
        SDL.Keycode.Delete => ClayTextInput.Key.Delete,
        SDL.Keycode.Return => ClayTextInput.Key.Enter,
        SDL.Keycode.Escape => ClayTextInput.Key.Escape,
        SDL.Keycode.A => ClayTextInput.Key.A,
        SDL.Keycode.C => ClayTextInput.Key.C,
        SDL.Keycode.V => ClayTextInput.Key.V,
        SDL.Keycode.X => ClayTextInput.Key.X,
        _ => null,
    };

    public static ClayTextInput.Mod MapMods(SDL.Keymod mods)
    {
        ClayTextInput.Mod result = ClayTextInput.Mod.None;
        if ((mods & SDL.Keymod.Shift) != 0) result |= ClayTextInput.Mod.Shift;
        if ((mods & SDL.Keymod.Ctrl) != 0) result |= ClayTextInput.Mod.Ctrl;
        if ((mods & SDL.Keymod.Alt) != 0) result |= ClayTextInput.Mod.Alt;
        if ((mods & SDL.Keymod.GUI) != 0) result |= ClayTextInput.Mod.Super;
        return result;
    }

    public static void ProcessEvent(ref SDL.Event e)
    {
        switch ((SDL.EventType)e.Type)
        {
            case SDL.EventType.TextInput:
            {
                string? text = Marshal.PtrToStringUTF8(e.Text.Text);
                if (string.IsNullOrEmpty(text)) break;
                foreach (Rune rune in text.AsSpan().EnumerateRunes())
                    ClayTextInput.OnChar((uint)rune.Value);
                break;
            }
            case SDL.EventType.KeyDown:
            {
                ClayTextInput.Key? key = MapKey(e.Key.Key);
                if (key.HasValue)
                {
                    ClayTextInput.Action action = e.Key.Repeat
                        ? ClayTextInput.Action.Repeat
                        : ClayTextInput.Action.Press;
                    ClayTextInput.OnKey(key.Value, action, MapMods(SDL.GetModState()));
                }
                break;
            }
        }
    }
}