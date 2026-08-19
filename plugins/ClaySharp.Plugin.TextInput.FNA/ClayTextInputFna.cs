using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Xna.Framework.Input;

namespace ClaySharp.Plugin.TextInput.FNA;

public static class ClayTextInputFna
{
    // Lazily created I-beam cursor; cached for the process lifetime.
    private static nint _ibeamCursor;

    public static ClayTextInput.Platform Platform()
    {
        return new ClayTextInput.Platform
        {
            GetClipboardText = _ => SDL3.SDL.SDL_GetClipboardText(),
            SetClipboardText = (_, text) => SDL3.SDL.SDL_SetClipboardText(text),
            SetIbeamCursor = _ =>
            {
                if (_ibeamCursor == 0)
                    _ibeamCursor = SDL3.SDL.SDL_CreateSystemCursor(SDL3.SDL.SDL_SystemCursor.SDL_SYSTEM_CURSOR_TEXT);
                SDL3.SDL.SDL_SetCursor(_ibeamCursor);
            },
            ResetCursor = _ => SDL3.SDL.SDL_SetCursor(SDL3.SDL.SDL_GetDefaultCursor()),
            UserData = null,
        };
    }

    public static ClayTextInput.Key? MapKey(SDL3.SDL.SDL_Keycode key) => key switch
    {
        SDL3.SDL.SDL_Keycode.SDLK_LEFT => ClayTextInput.Key.Left,
        SDL3.SDL.SDL_Keycode.SDLK_RIGHT => ClayTextInput.Key.Right,
        SDL3.SDL.SDL_Keycode.SDLK_HOME => ClayTextInput.Key.Home,
        SDL3.SDL.SDL_Keycode.SDLK_END => ClayTextInput.Key.End,
        SDL3.SDL.SDL_Keycode.SDLK_BACKSPACE => ClayTextInput.Key.Backspace,
        SDL3.SDL.SDL_Keycode.SDLK_DELETE => ClayTextInput.Key.Delete,
        SDL3.SDL.SDL_Keycode.SDLK_RETURN => ClayTextInput.Key.Enter,
        SDL3.SDL.SDL_Keycode.SDLK_ESCAPE => ClayTextInput.Key.Escape,
        SDL3.SDL.SDL_Keycode.SDLK_A => ClayTextInput.Key.A,
        SDL3.SDL.SDL_Keycode.SDLK_C => ClayTextInput.Key.C,
        SDL3.SDL.SDL_Keycode.SDLK_V => ClayTextInput.Key.V,
        SDL3.SDL.SDL_Keycode.SDLK_X => ClayTextInput.Key.X,
        _ => null,
    };

    public static ClayTextInput.Mod MapMods(SDL3.SDL.SDL_Keymod mods)
    {
        ClayTextInput.Mod result = ClayTextInput.Mod.None;
        if ((mods & SDL3.SDL.SDL_Keymod.SDL_KMOD_SHIFT) != 0) result |= ClayTextInput.Mod.Shift;
        if ((mods & SDL3.SDL.SDL_Keymod.SDL_KMOD_CTRL) != 0) result |= ClayTextInput.Mod.Ctrl;
        if ((mods & SDL3.SDL.SDL_Keymod.SDL_KMOD_ALT) != 0) result |= ClayTextInput.Mod.Alt;
        if ((mods & SDL3.SDL.SDL_Keymod.SDL_KMOD_GUI) != 0) result |= ClayTextInput.Mod.Super;
        return result;
    }

    public static unsafe void HookEvents()
    {
        SDL3.SDL.SDL_AddEventWatch((data, ev) =>
        {
            switch ((SDL3.SDL.SDL_EventType)ev->type)
            {
                case SDL3.SDL.SDL_EventType.SDL_EVENT_TEXT_INPUT:
                {
                    string? text = Marshal.PtrToStringUTF8((nint)ev->text.text);
                    if (string.IsNullOrEmpty(text)) break;
                    foreach (Rune rune in text.AsSpan().EnumerateRunes())
                        ClayTextInput.OnChar((uint)rune.Value);
                    break;
                }
                case SDL3.SDL.SDL_EventType.SDL_EVENT_KEY_DOWN:
                {
                    ClayTextInput.Key? key = MapKey((SDL3.SDL.SDL_Keycode)ev->key.key);
                    if (key.HasValue)
                    {
                        ClayTextInput.Action action = ev->key.repeat
                            ? ClayTextInput.Action.Repeat
                            : ClayTextInput.Action.Press;
                        ClayTextInput.OnKey(key.Value, action, MapMods(SDL3.SDL.SDL_GetModState()));
                    }
                    break;
                }
            }
            
            return false;
        }, 0);
    }
}