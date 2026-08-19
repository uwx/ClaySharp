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

namespace ClaySharp.Plugin.TextInput.SDL3
{
    public static class ClayTextInputSdl3
    {
        // Lazily created I-beam cursor; cached for the process lifetime.
        private static nint s_ibeamCursor;

        public static Clay_TextInput_Platform Platform()
        {
            return new Clay_TextInput_Platform
            {
                getClipboardText = _ => SDL.GetClipboardText(),
                setClipboardText = (_, text) => SDL.SetClipboardText(text),
                setIbeamCursor = _ =>
                {
                    if (s_ibeamCursor == 0)
                        s_ibeamCursor = SDL.CreateSystemCursor(SDL.SystemCursor.Text);
                    SDL.SetCursor(s_ibeamCursor);
                },
                resetCursor = _ => SDL.SetCursor(SDL.GetDefaultCursor()),
                userData = null,
            };
        }

        public static Clay_TI_Key? MapKey(SDL.Keycode key) => key switch
        {
            SDL.Keycode.Left => Clay_TI_Key.CLAY_TI_KEY_LEFT,
            SDL.Keycode.Right => Clay_TI_Key.CLAY_TI_KEY_RIGHT,
            SDL.Keycode.Home => Clay_TI_Key.CLAY_TI_KEY_HOME,
            SDL.Keycode.End => Clay_TI_Key.CLAY_TI_KEY_END,
            SDL.Keycode.Backspace => Clay_TI_Key.CLAY_TI_KEY_BACKSPACE,
            SDL.Keycode.Delete => Clay_TI_Key.CLAY_TI_KEY_DELETE,
            SDL.Keycode.Return => Clay_TI_Key.CLAY_TI_KEY_ENTER,
            SDL.Keycode.Escape => Clay_TI_Key.CLAY_TI_KEY_ESCAPE,
            SDL.Keycode.A => Clay_TI_Key.CLAY_TI_KEY_A,
            SDL.Keycode.C => Clay_TI_Key.CLAY_TI_KEY_C,
            SDL.Keycode.V => Clay_TI_Key.CLAY_TI_KEY_V,
            SDL.Keycode.X => Clay_TI_Key.CLAY_TI_KEY_X,
            _ => null,
        };

        public static Clay_TI_Mod MapMods(SDL.Keymod mods)
        {
            Clay_TI_Mod result = Clay_TI_Mod.CLAY_TI_MOD_NONE;
            if ((mods & SDL.Keymod.Shift) != 0) result |= Clay_TI_Mod.CLAY_TI_MOD_SHIFT;
            if ((mods & SDL.Keymod.Ctrl) != 0) result |= Clay_TI_Mod.CLAY_TI_MOD_CTRL;
            if ((mods & SDL.Keymod.Alt) != 0) result |= Clay_TI_Mod.CLAY_TI_MOD_ALT;
            if ((mods & SDL.Keymod.GUI) != 0) result |= Clay_TI_Mod.CLAY_TI_MOD_SUPER;
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
                    Clay_TI_Key? key = MapKey(e.Key.Key);
                    if (key.HasValue)
                    {
                        Clay_TI_Action action = e.Key.Repeat
                            ? Clay_TI_Action.CLAY_TI_ACTION_REPEAT
                            : Clay_TI_Action.CLAY_TI_ACTION_PRESS;
                        ClayTextInput.OnKey(key.Value, action, MapMods(SDL.GetModState()));
                    }
                    break;
                }
            }
        }
    }
}
