using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

namespace Molca.Editor.Mcp.Assistant
{
    /// <summary>
    /// Persists the assistant conversation (visible transcript, LLM history, and pinned context) across
    /// window close and domain reload (Sprint 24.5). Stored off-asset under <c>Library/Molca/</c> — it is
    /// session state, never project content, and is intentionally excluded from version control.
    /// </summary>
    public static class AssistantSessionStore
    {
        private static string SessionPath =>
            Path.Combine(
                Path.GetDirectoryName(Application.dataPath) ?? ".",
                "Library", "Molca", "assistant-session.json");

        /// <summary>Writes the current conversation to disk, replacing any prior session.</summary>
        public static void Save(
            IReadOnlyList<ChatTurn> transcript,
            IReadOnlyList<LlmMessage> history,
            IReadOnlyList<AssistantContextItem> context)
        {
            try
            {
                var dto = new SessionDto
                {
                    Turns = ToTurnDtos(transcript),
                    History = ToMessageDtos(history),
                    Context = context != null ? new List<AssistantContextItem>(context) : new List<AssistantContextItem>()
                };

                var dir = Path.GetDirectoryName(SessionPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(SessionPath, JsonConvert.SerializeObject(dto));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to save assistant session: {ex.Message}");
            }
        }

        /// <summary>Loads a persisted conversation, if one exists and parses cleanly.</summary>
        public static bool TryLoad(
            out List<ChatTurn> transcript,
            out List<LlmMessage> history,
            out List<AssistantContextItem> context)
        {
            transcript = new List<ChatTurn>();
            history = new List<LlmMessage>();
            context = new List<AssistantContextItem>();

            try
            {
                if (!File.Exists(SessionPath)) return false;
                var dto = JsonConvert.DeserializeObject<SessionDto>(File.ReadAllText(SessionPath));
                if (dto == null) return false;

                transcript = FromTurnDtos(dto.Turns);
                history = FromMessageDtos(dto.History);
                context = dto.Context ?? new List<AssistantContextItem>();
                return transcript.Count > 0 || history.Count > 0 || context.Count > 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to load assistant session: {ex.Message}");
                return false;
            }
        }

        /// <summary>Deletes the persisted session.</summary>
        public static void Clear()
        {
            try
            {
                if (File.Exists(SessionPath)) File.Delete(SessionPath);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to clear assistant session: {ex.Message}");
            }
        }

        /// <summary>Absolute path of the legacy single-session file, used for one-time migration.</summary>
        public static string LegacySessionPath => SessionPath;

        /// <summary>Serializes a conversation (with a <see cref="SessionMeta"/> header) to an arbitrary path.</summary>
        public static void SaveToPath(
            string path,
            IReadOnlyList<ChatTurn> transcript,
            IReadOnlyList<LlmMessage> history,
            IReadOnlyList<AssistantContextItem> context,
            SessionMeta meta)
        {
            try
            {
                var dto = new SessionDto
                {
                    Meta = meta,
                    Turns = ToTurnDtos(transcript),
                    History = ToMessageDtos(history),
                    Context = context != null ? new List<AssistantContextItem>(context) : new List<AssistantContextItem>()
                };
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonConvert.SerializeObject(dto));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to save assistant session to {path}: {ex.Message}");
            }
        }

        /// <summary>Loads a conversation and its <see cref="SessionMeta"/> from an arbitrary path.</summary>
        public static bool TryLoadFromPath(
            string path,
            out List<ChatTurn> transcript,
            out List<LlmMessage> history,
            out List<AssistantContextItem> context,
            out SessionMeta meta)
        {
            transcript = new List<ChatTurn>();
            history = new List<LlmMessage>();
            context = new List<AssistantContextItem>();
            meta = null;
            try
            {
                if (!File.Exists(path)) return false;
                var dto = JsonConvert.DeserializeObject<SessionDto>(File.ReadAllText(path));
                if (dto == null) return false;
                transcript = FromTurnDtos(dto.Turns);
                history = FromMessageDtos(dto.History);
                context = dto.Context ?? new List<AssistantContextItem>();
                meta = dto.Meta;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Molca] Failed to load assistant session from {path}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Reads only the <see cref="SessionMeta"/> header from a session file (for listing).</summary>
        public static bool TryReadMeta(string path, out SessionMeta meta)
        {
            meta = null;
            try
            {
                if (!File.Exists(path)) return false;
                var dto = JsonConvert.DeserializeObject<SessionDto>(File.ReadAllText(path));
                meta = dto?.Meta;
                return meta != null;
            }
            catch
            {
                return false;
            }
        }

        private static List<TurnDto> ToTurnDtos(IReadOnlyList<ChatTurn> transcript)
        {
            var list = new List<TurnDto>();
            if (transcript == null) return list;
            foreach (var t in transcript)
            {
                if (t == null) continue;
                var dto = new TurnDto
                {
                    Kind = (int)t.Kind,
                    Text = t.Text,
                    Tools = new List<ToolSummaryDto>(),
                    WorkItems = t.WorkItems != null ? new List<string>(t.WorkItems) : new List<string>(),
                    PromptAnswer = t.PromptAnswer,
                    IsConfirmation = t.IsConfirmation,
                    Detail = t.Detail,
                    CanPin = t.CanPin
                };
                if (t.ToolSummaries != null)
                {
                    foreach (var s in t.ToolSummaries)
                    {
                        if (s == null) continue;
                        dto.Tools.Add(new ToolSummaryDto
                        {
                            Name = s.Name, Args = s.ArgumentsJson, Result = s.ResultContent,
                            IsError = s.IsError, Mode = s.Mode, ToolKind = s.Kind,
                            Reversibility = s.Reversibility, UndoId = s.UndoEntryId, UndoGroup = s.UndoGroup
                        });
                    }
                }
                list.Add(dto);
            }
            return list;
        }

        private static List<ChatTurn> FromTurnDtos(List<TurnDto> dtos)
        {
            var list = new List<ChatTurn>();
            if (dtos == null) return list;
            foreach (var d in dtos)
            {
                if (d == null) continue;
                IReadOnlyList<ChatToolSummary> summaries = null;
                if (d.Tools != null && d.Tools.Count > 0)
                {
                    var s = new List<ChatToolSummary>();
                    foreach (var td in d.Tools)
                        s.Add(new ChatToolSummary(td.Name, td.Args, td.Result, td.IsError, td.Mode, td.ToolKind,
                            string.IsNullOrEmpty(td.Reversibility) ? "Unknown" : td.Reversibility, td.UndoId, td.UndoGroup));
                    summaries = s;
                }
                list.Add(new ChatTurn((ChatTurnKind)d.Kind, d.Text, summaries, -1, d.WorkItems)
                {
                    PromptAnswer = d.PromptAnswer,
                    IsConfirmation = d.IsConfirmation,
                    Detail = d.Detail,
                    CanPin = d.CanPin
                });
            }
            return list;
        }

        private static List<MessageDto> ToMessageDtos(IReadOnlyList<LlmMessage> history)
        {
            var list = new List<MessageDto>();
            if (history == null) return list;
            foreach (var m in history)
            {
                if (m == null) continue;
                var dto = new MessageDto { Role = (int)m.Role, Text = m.Text, Calls = new List<ToolCallDto>(), Results = new List<ToolResultDto>() };
                if (m.ToolCalls != null)
                    foreach (var c in m.ToolCalls)
                        dto.Calls.Add(new ToolCallDto { Id = c.Id, Name = c.Name, Args = c.ArgumentsJson });
                if (m.ToolResults != null)
                    foreach (var r in m.ToolResults)
                        dto.Results.Add(new ToolResultDto { Id = r.ToolCallId, Content = r.Content, IsError = r.IsError });
                list.Add(dto);
            }
            return list;
        }

        private static List<LlmMessage> FromMessageDtos(List<MessageDto> dtos)
        {
            var list = new List<LlmMessage>();
            if (dtos == null) return list;
            foreach (var d in dtos)
            {
                if (d == null) continue;
                var m = new LlmMessage { Role = (LlmRole)d.Role, Text = d.Text };
                if (d.Calls != null)
                    foreach (var c in d.Calls)
                        m.ToolCalls.Add(new LlmToolCall(c.Id, c.Name, c.Args));
                if (d.Results != null)
                    foreach (var r in d.Results)
                        m.ToolResults.Add(new LlmToolResult(r.Id, r.Content, r.IsError));
                list.Add(m);
            }
            return list;
        }

        [Serializable] private sealed class SessionDto
        {
            public SessionMeta Meta;
            public List<TurnDto> Turns = new List<TurnDto>();
            public List<MessageDto> History = new List<MessageDto>();
            public List<AssistantContextItem> Context = new List<AssistantContextItem>();
        }

        [Serializable] private sealed class TurnDto
        {
            public int Kind;
            public string Text;
            public List<ToolSummaryDto> Tools = new List<ToolSummaryDto>();
            public List<string> WorkItems = new List<string>();
            public string PromptAnswer;
            public bool IsConfirmation;
            public string Detail;
            public bool CanPin;
        }

        [Serializable] private sealed class ToolSummaryDto
        {
            public string Name;
            public string Args;
            public string Result;
            public bool IsError;
            public string Mode;
            public string ToolKind;
            public string Reversibility;
            public string UndoId;
            public int UndoGroup = -1;
        }

        [Serializable] private sealed class MessageDto
        {
            public int Role;
            public string Text;
            public List<ToolCallDto> Calls = new List<ToolCallDto>();
            public List<ToolResultDto> Results = new List<ToolResultDto>();
        }

        [Serializable] private sealed class ToolCallDto
        {
            public string Id;
            public string Name;
            public string Args;
        }

        [Serializable] private sealed class ToolResultDto
        {
            public string Id;
            public string Content;
            public bool IsError;
        }
    }
}
