using System.Linq;
using System;
using System.Collections.Generic;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using static TLDRevamp.Net.MpScreen;

namespace TLDRevamp.Net
{
    internal static class MpScreenTools
    {

        /// Diagnostics / test hooks (bridge `mp screen [open|close|tile i|primary|secondary|kick|yes|mute|text <s>]`; kick
        /// is Forget on a saved player).
        public static string Test(string arg)
        {
            var a = arg.Split(new[] { ' ' }, 2);
            switch (a[0])
            {
                case "open": Toggle(); if (!IsOpen) Open(); break;
                case "close": Close(); break;
                case "tile": SelectTile(int.Parse(a[1])); break;
                case "primary": Primary(); break;
                case "secondary": Secondary(); break;
                case "kick": AskKick(); break;
                case "yes": Yes(); break;
                case "no": No(); break;
                case "mute": MuteOrRefresh(); break;
                case "text": TLDRevamp.Net.MpScreen.ObservedInput.SetTextWithoutNotify(a.Length > 1 ? a[1] : ""); break;
                case "settings": TLDRevamp.Net.MpScreen.ObservedSettingsOpen = !TLDRevamp.Net.MpScreen.ObservedSettingsOpen; break;
                case "setting":
                    if (a.Length > 1 && int.TryParse(a[1], out int row)) ChangeSessionSetting(row);
                    break;
                case "password":
                    TLDRevamp.Net.MpScreen.ObservedSettingsPassword.SetTextWithoutNotify(a.Length > 1 ? a[1] : "");
                    CommitPassword();
                    break;
            }
            if (IsOpen) Refresh();
            var tiles = new List<string>();
            foreach (var tl in TLDRevamp.Net.MpScreen.ObservedTiles) if (tl.Go.activeSelf) tiles.Add(Json.Str(tl.Name.text));
            var lines = new List<string>();
            foreach (var l in TLDRevamp.Net.MpScreen.ObservedCardLines) lines.Add(Json.Str(l.text));
            return "{\"open\":" + (IsOpen ? "true" : "false") + ",\"title\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedTitle?.text) + ",\"count\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedCount?.text) +
                   ",\"tiles\":[" + string.Join(",", tiles) + "],\"card\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedCardName?.text) + ",\"cardRight\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedCardRight?.text) +
                   ",\"lines\":[" + string.Join(",", lines) + "],\"primary\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedPrimary != null && TLDRevamp.Net.MpScreen.ObservedPrimary.gameObject.activeSelf ? TLDRevamp.Net.MpScreen.ObservedPrimary.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"secondary\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedSecondary != null && TLDRevamp.Net.MpScreen.ObservedSecondary.gameObject.activeSelf ? TLDRevamp.Net.MpScreen.ObservedSecondary.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"kick\":" + (TLDRevamp.Net.MpScreen.ObservedKick != null && TLDRevamp.Net.MpScreen.ObservedKick.gameObject.activeSelf ? "true" : "false") +
                   ",\"mute\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedMute != null && TLDRevamp.Net.MpScreen.ObservedMute.gameObject.activeSelf ? TLDRevamp.Net.MpScreen.ObservedMute.GetComponentInChildren<TextMeshProUGUI>().text : null) +
                   ",\"confirm\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedConfirm != null && TLDRevamp.Net.MpScreen.ObservedConfirm.activeSelf ? TLDRevamp.Net.MpScreen.ObservedConfirmText.text : null) +
                   ",\"input\":" + (TLDRevamp.Net.MpScreen.ObservedInput != null && TLDRevamp.Net.MpScreen.ObservedInput.gameObject.activeSelf ? Json.Str(TLDRevamp.Net.MpScreen.ObservedInputPlaceholder != null ? TLDRevamp.Net.MpScreen.ObservedInputPlaceholder.text : "") : "null") +
                   ",\"settingsOpen\":" + (TLDRevamp.Net.MpScreen.ObservedSettingsPanel != null && TLDRevamp.Net.MpScreen.ObservedSettingsPanel.activeSelf ? "true" : "false") +
                   ",\"settingRows\":[" + (TLDRevamp.Net.MpScreen.ObservedSettingsRows[0] != null ? Json.Str(TLDRevamp.Net.MpScreen.ObservedSettingsRows[0].GetComponentInChildren<TextMeshProUGUI>(true).text) : "null") + "," +
                   (TLDRevamp.Net.MpScreen.ObservedSettingsRows[1] != null ? Json.Str(TLDRevamp.Net.MpScreen.ObservedSettingsRows[1].GetComponentInChildren<TextMeshProUGUI>(true).text) : "null") + "," +
                   (TLDRevamp.Net.MpScreen.ObservedSettingsRows[2] != null ? Json.Str(TLDRevamp.Net.MpScreen.ObservedSettingsRows[2].GetComponentInChildren<TextMeshProUGUI>(true).text) : "null") + "," +
                   (TLDRevamp.Net.MpScreen.ObservedSettingsRows[3] != null ? Json.Str(TLDRevamp.Net.MpScreen.ObservedSettingsRows[3].GetComponentInChildren<TextMeshProUGUI>(true).text) : "null") + "," +
                   (TLDRevamp.Net.MpScreen.ObservedSettingsRows[4] != null ? Json.Str(TLDRevamp.Net.MpScreen.ObservedSettingsRows[4].GetComponentInChildren<TextMeshProUGUI>(true).text) : "null") + "]" +
                   ",\"settingsHostEditable\":" + (TLDRevamp.Net.MpScreen.ObservedSettingsRows[0] != null && TLDRevamp.Net.MpScreen.ObservedSettingsRows[0].interactable ? "true" : "false") +
                   ",\"message\":" + Json.Str(TLDRevamp.Net.MpScreen.ObservedMsg != null && TLDRevamp.Net.MpScreen.ObservedMsg.gameObject.activeSelf ? TLDRevamp.Net.MpScreen.ObservedMsg.text : null) + "}";
        }
    }
}
