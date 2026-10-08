using System;
using UnityEngine;

namespace Tribe
{
    // While tribe.exe's menu is up, the game lets go of the mouse so the menu can be clicked without pausing.
    // Four of the game's own mechanisms, each undone on close, unload and link loss:
    //   CursorManager.ShowCursor(true)         a cursor request, same as its inventory/notebook; counted, so paired
    //   InputsManager.m_TextInputActive        what the chat box sets: drops every input action (no swings on click)
    //                                          and blocks movement (Player.GetMovesBlocked)
    //   Player.BlockRotation()                 stops mouse look while the cursor travels to the menu; counted, tied
    //                                          to the Player object we blocked
    //   Application.runInBackground = true     clicking the menu takes focus from the game; it must keep running
    static class MenuHold
    {
        static bool cursorHeld, textInputHeld, backgroundHeld, backgroundWas;
        static Player rotationBlocked;

        /// <summary>Called every frame the menu is open: takes whatever isn't held yet (the player object appears
        /// when a world loads and changes on reload).</summary>
        public static void Hold()
        {
            try
            {
                var cm = CursorManager.Get();
                if (!cursorHeld && cm != null) { cm.ShowCursor(true); cursorHeld = true; }
            }
            catch (Exception e) { Debug.LogWarning("[tribe] " + "cursor request failed: " + e.Message); }
            try
            {
                var im = InputsManager.Get();
                if (!textInputHeld && im != null && !im.m_TextInputActive) { im.m_TextInputActive = true; textInputHeld = true; }
            }
            catch { }
            try
            {
                var p = Player.Get();
                if (p != rotationBlocked)
                {
                    if (rotationBlocked != null) rotationBlocked.UnblockRotation();
                    rotationBlocked = null;
                    if (p != null) { p.BlockRotation(); rotationBlocked = p; }
                }
            }
            catch (Exception e) { Debug.LogWarning("[tribe] " + "rotation block failed: " + e.Message); }
            if (!backgroundHeld) { backgroundWas = Application.runInBackground; Application.runInBackground = true; backgroundHeld = true; }
        }

        public static void Release()
        {
            if (cursorHeld)
            {
                cursorHeld = false;
                try { var cm = CursorManager.Get(); if (cm != null) cm.ShowCursor(false); } catch { }
            }
            if (textInputHeld)
            {
                textInputHeld = false;
                try { var im = InputsManager.Get(); if (im != null) im.m_TextInputActive = false; } catch { }
            }
            if (rotationBlocked != null)
            {
                try { rotationBlocked.UnblockRotation(); } catch { }
            }
            rotationBlocked = null;
            if (backgroundHeld) { backgroundHeld = false; Application.runInBackground = backgroundWas; }
        }
    }
}
