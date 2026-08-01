using System;
using System.Collections.Generic;
using System.Text;
using Molca.Editor.Networking.RequestConsole;
using Molca.Editor.UI.Components;
using Molca.Networking.Configuration;
using Molca.Networking.Diagnostics;
using Molca.Networking.Http.Models;
using Molca.Networking.Pipeline;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Molca.Editor.Networking.Hub.Views
{
    /// <summary>
    /// The request console: compose a request against a route, read what it will actually do, send it,
    /// and read the redacted result.
    /// </summary>
    /// <remarks>
    /// Three panes stacked in one column — <b>compose</b>, <b>preflight</b>, <b>result</b> — because the
    /// preflight sits between the two on purpose. It is not a collapsible detail; it is the answer to
    /// "where is this going, as whom, under what policy", and it is rendered from the same resolver the
    /// runtime uses (see <see cref="NetworkConsolePreflight"/>).
    /// <para>
    /// Nothing here can address a host the catalog did not bind, and nothing here persists a header or a
    /// body. A production mutation is confirmed per send, and only when the catalog opted into allowing
    /// one at all.
    /// </para>
    /// </remarks>
    internal sealed class NetworkConsoleView : VisualElement
    {
        private readonly NetworkHubSession _session;
        private readonly NetworkConsoleRequest _draft;

        private readonly VisualElement _composeBody = new VisualElement();
        private readonly VisualElement _preflightBody = new VisualElement();
        private readonly VisualElement _resultBody = new VisualElement();
        private readonly VisualElement _historyBody = new VisualElement();

        private MolcaSectionCard _preflightCard;
        private MolcaSectionCard _resultCard;
        private Button _sendButton;
        private Button _cancelButton;

        private NetworkConsolePreflight _preflight;
        private bool _wasCancelled;

        /// <summary>Builds the view.</summary>
        /// <param name="session">The workspace session.</param>
        internal NetworkConsoleView(NetworkHubSession session)
        {
            _session = session;
            _draft = session.Draft;

            AddToClassList("molca-network__view");
            style.flexGrow = 1;

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            Add(scroll);

            scroll.Add(BuildCompose());
            scroll.Add(BuildPreflight());
            scroll.Add(BuildResult());
            scroll.Add(BuildHistory());

            _session.Console.Changed += OnRunnerChanged;
            RegisterCallback<DetachFromPanelEvent>(_ => _session.Console.Changed -= OnRunnerChanged);

            RefreshCompose();
            RefreshPreflight();
            RefreshResult();
            RefreshHistory();
        }

        private void OnRunnerChanged()
        {
            RefreshResult();
            RefreshHistory();
            RefreshSendState();
        }

        #region Compose

        private VisualElement BuildCompose()
        {
            var card = NetworkHubUi.Card("Request", "Composed against a route, never a raw URL");
            card.Body.Add(_composeBody);
            return card;
        }

        private void RefreshCompose()
        {
            _composeBody.Clear();

            var environments = Ids(_session.Catalog.Environments, e => e?.Id);
            var services = Ids(_session.Catalog.Services, s => s?.Id);

            if (environments.Count == 0 || services.Count == 0)
            {
                _composeBody.Add(NetworkHubUi.Note(
                    "The console needs at least one environment and one service. Authoring them in " +
                    "Environments and Services is what gives a request somewhere to go."));
                return;
            }

            _composeBody.Add(Popup("Environment", environments, _draft.EnvironmentId, value =>
            {
                _draft.EnvironmentId = value;
                OnDraftChanged();
            }));

            _composeBody.Add(Popup("Service", services, _draft.ServiceId, value =>
            {
                _draft.ServiceId = value;
                _draft.AdoptEndpoint(null);
                OnDraftChanged();
            }));

            _composeBody.Add(BuildEndpointSelector());

            var method = new EnumField("Method", _draft.Method);
            method.RegisterValueChangedCallback(evt =>
            {
                _draft.Method = (HttpMethod)evt.newValue;
                OnDraftChanged();
            });
            _composeBody.Add(method);

            var path = new TextField("Path") { value = _draft.RelativePath };
            path.tooltip =
                "Relative to the service's bound origin for the selected environment. The origin is not " +
                "editable here — it is a property of the catalog binding.";
            path.RegisterCallback<BlurEvent>(_ =>
            {
                _draft.RelativePath = path.value;
                OnDraftChanged();
            });
            _composeBody.Add(path);

            _composeBody.Add(NetworkHubUi.Heading("Path parameters"));
            _composeBody.Add(EntryList(_draft.PathParameters, "name", "value"));

            _composeBody.Add(NetworkHubUi.Heading("Query parameters"));
            _composeBody.Add(EntryList(_draft.QueryParameters, "name", "value"));

            _composeBody.Add(NetworkHubUi.Heading("Headers"));
            _composeBody.Add(NetworkHubUi.Note(
                "Headers are held in memory only. They are never written to preferences, never recorded " +
                "in history, and never exported."));
            _composeBody.Add(EntryList(_draft.Headers, "header", "value"));

            _composeBody.Add(BuildBodyEditor());
            _composeBody.Add(BuildSendOptions());
            _composeBody.Add(BuildActions());
        }

        private VisualElement BuildEndpointSelector()
        {
            var service = _session.Catalog.FindService(_draft.ServiceId);
            var endpoints = EndpointsOf(service);

            var labels = new List<string> { "(ad-hoc path)" };
            foreach (var endpoint in endpoints)
                labels.Add(endpoint.Id);

            string current = _draft.UsesEndpoint && labels.Contains(_draft.EndpointId)
                ? _draft.EndpointId
                : labels[0];

            return Popup("Endpoint", labels, current, value =>
            {
                _draft.AdoptEndpoint(value == labels[0] ? null : Find(endpoints, value));
                OnDraftChanged();
            });
        }

        private VisualElement BuildBodyEditor()
        {
            var container = new VisualElement();
            container.Add(NetworkHubUi.Heading("Body"));

            var kind = new EnumField("Type", _draft.BodyType);
            kind.RegisterValueChangedCallback(evt =>
            {
                _draft.BodyType = (BodyType)evt.newValue;
                OnDraftChanged();
            });
            container.Add(kind);

            switch (_draft.BodyType)
            {
                case BodyType.Json:
                    var json = new TextField { value = _draft.Body, multiline = true };
                    json.AddToClassList("molca-network__code");
                    json.RegisterCallback<BlurEvent>(_ =>
                    {
                        _draft.Body = json.value;
                        OnDraftChanged();
                    });
                    container.Add(json);
                    break;

                case BodyType.Form:
                    container.Add(EntryList(_draft.FormFields, "field", "value"));
                    break;

                case BodyType.Binary:
                    container.Add(NetworkHubUi.Note(
                        "Binary bodies are not composable here. Send one from code, or use a form field."));
                    break;
            }

            return container;
        }

        private VisualElement BuildSendOptions()
        {
            var container = new VisualElement();
            container.Add(NetworkHubUi.Heading("This send"));

            var idempotency = new TextField("Idempotency key") { value = _draft.IdempotencyKey };
            idempotency.tooltip =
                "Sent as the Idempotency-Key header. A mutating endpoint that declares it needs one is " +
                "telling you a retry could otherwise apply the change twice.";
            idempotency.RegisterCallback<BlurEvent>(_ =>
            {
                _draft.IdempotencyKey = idempotency.value;
                OnDraftChanged();
            });
            container.Add(idempotency);

            var retry = new Toggle("Retry") { value = _draft.RetryEnabledOverride ?? true };
            retry.tooltip = "Overrides the effective policy's retry switch for this send only.";
            retry.RegisterValueChangedCallback(evt =>
            {
                _draft.RetryEnabledOverride = evt.newValue;
                OnDraftChanged();
            });
            container.Add(retry);

            var capture = new Toggle("Record response body in history") { value = _draft.CaptureBody };
            capture.tooltip =
                "Off by default. The preview is redacted for credential-shaped JSON fields, but a response " +
                "body is whatever the server chose to return, so retaining one is a decision.";
            capture.RegisterValueChangedCallback(evt =>
            {
                _draft.CaptureBody = evt.newValue;
                OnDraftChanged();
            });
            container.Add(capture);

            // Per-send policy relaxations stop here. There is deliberately no TLS-validation toggle and no
            // allowed-host override: a lower-precedence layer may tighten a security rule but never weaken
            // one, and a console control that could weaken it would be a hole in exactly that rule.
            container.Add(NetworkHubUi.Note(
                "Transport safety is not overridable from here. TLS validation, allowed hosts, and " +
                "redirect rules resolve tighten-only, so this panel cannot weaken them."));

            return container;
        }

        private VisualElement BuildActions()
        {
            _sendButton = MolcaButtons.Primary("Send", Send);
            _cancelButton = MolcaButtons.Mini("Cancel", () => _session.Console.Cancel());

            var actions = NetworkHubUi.Actions(
                _sendButton,
                _cancelButton,
                MolcaButtons.Mini("Reset", () =>
                {
                    _draft.AdoptEndpoint(null);
                    _draft.Headers.Clear();
                    _draft.FormFields.Clear();
                    _draft.Body = string.Empty;
                    _draft.IdempotencyKey = string.Empty;
                    OnDraftChanged();
                }));

            RefreshSendState();
            return actions;
        }

        private void RefreshSendState()
        {
            if (_sendButton == null) return;

            bool sending = _session.Console.IsSending;
            _sendButton.SetEnabled(!sending && _preflight != null && _preflight.CanSend);
            _sendButton.text = sending ? "Sending…" : _preflight != null && _preflight.RequiresConfirmation
                ? "Send…"
                : "Send";

            _cancelButton.SetEnabled(sending);
        }

        private void OnDraftChanged()
        {
            RefreshCompose();
            RefreshPreflight();
            RefreshSendState();
        }

        #endregion

        #region Preflight

        private VisualElement BuildPreflight()
        {
            _preflightCard = NetworkHubUi.Card("Preflight", "What this request will actually do");
            _preflightCard.Body.Add(_preflightBody);
            return _preflightCard;
        }

        private void RefreshPreflight()
        {
            _preflightBody.Clear();
            _preflight = NetworkConsolePreflight.Evaluate(_session.Catalog, _draft);

            _preflightBody.Add(NetworkHubUi.Field("Destination", _preflight.RedactedUri,
                "Query values are masked here. The request sends them unmasked — masking is for display, " +
                "history, and export."));

            var resolution = _preflight.Resolution;
            if (resolution != null && resolution.Resolves)
            {
                _preflightBody.Add(NetworkHubUi.Field("Host", resolution.Host));
                _preflightBody.Add(NetworkHubUi.Field("Environment",
                    $"{resolution.Environment?.Id}{(resolution.IsProduction ? "  ·  production safety enforced" : string.Empty)}"));

                _preflightBody.Add(NetworkHubUi.Field("Credential",
                    string.IsNullOrEmpty(_preflight.CredentialProfileId)
                        ? "anonymous"
                        : _preflight.CredentialWillBeSent
                            ? $"{_preflight.CredentialProfileId} (attached)"
                            : $"{_preflight.CredentialProfileId} (withheld)",
                    "The profile's name only. The console never displays, stores, or exports a credential value."));

                var policy = _preflight.Policy;
                if (policy != null)
                {
                    _preflightBody.Add(NetworkHubUi.EffectiveField(
                        "Timeout", $"{policy.OverallTimeoutSeconds.Value:0.##}s overall / " +
                                   $"{policy.AttemptTimeoutSeconds.Value:0.##}s per attempt",
                        policy.OverallTimeoutSeconds.Source));

                    _preflightBody.Add(NetworkHubUi.EffectiveField(
                        "Retry",
                        policy.RetryEnabled.Value ? $"up to {policy.MaxRetries.Value} retries" : "disabled",
                        policy.RetryEnabled.Source));

                    _preflightBody.Add(NetworkHubUi.EffectiveField(
                        "Redirects",
                        $"{policy.RedirectMode.Value} · max {policy.MaxRedirects.Value}",
                        policy.RedirectMode.Source));

                    _preflightBody.Add(NetworkHubUi.EffectiveField(
                        "Cache", $"{policy.CacheMode.Value}", policy.CacheMode.Source));
                }
            }

            foreach (var note in _preflight.Notes)
            {
                var row = new VisualElement();
                row.AddToClassList("molca-network__finding-header");
                row.Add(NetworkHubUi.Dot(StatusOf(note.Level), note.Level.ToString()));

                var message = new Label(note.Message);
                message.AddToClassList("molca-network__finding-message");
                message.style.whiteSpace = WhiteSpace.Normal;
                message.tooltip = note.Code;
                row.Add(message);

                _preflightBody.Add(row);
            }

            var status = !_preflight.CanSend ? MolcaStatusKind.Error
                : _preflight.NotesAtLeast(NetworkConsoleNoteLevel.Warning).Count > 0 ? MolcaStatusKind.Warning
                : MolcaStatusKind.Ok;

            _preflightCard.SetStatus(status, !_preflight.CanSend ? "Cannot send"
                : _preflight.RequiresConfirmation ? "Confirmation required"
                : "Ready");
        }

        private static MolcaStatusKind StatusOf(NetworkConsoleNoteLevel level)
        {
            switch (level)
            {
                case NetworkConsoleNoteLevel.Blocking: return MolcaStatusKind.Error;
                case NetworkConsoleNoteLevel.Warning: return MolcaStatusKind.Warning;
                default: return MolcaStatusKind.Idle;
            }
        }

        #endregion

        #region Send

        private async void Send()
        {
            if (_preflight == null || !_preflight.CanSend || _session.Console.IsSending)
                return;

            if (_preflight.RequiresConfirmation &&
                !EditorUtility.DisplayDialog("Send to production?", _preflight.ConfirmationMessage,
                    "Send", "Cancel"))
            {
                return;
            }

            _wasCancelled = false;
            var route = _draft.Route;

            using var activity = NetworkActivityTracker.Begin(
                "console-send", "Network", $"{_draft.Method} {route}",
                NetworkHubNavigationTarget.Console(),
                () => _session.Console.Cancel());

            var outcome = await _session.Console.SendAsync(_draft);
            _wasCancelled = outcome == null;

            RefreshResult();
            RefreshHistory();
            RefreshSendState();
        }

        #endregion

        #region Result

        private VisualElement BuildResult()
        {
            _resultCard = NetworkHubUi.Card("Result", "Redacted");
            _resultCard.Body.Add(_resultBody);
            return _resultCard;
        }

        private void RefreshResult()
        {
            _resultBody.Clear();

            var outcome = _session.Console.LastOutcome;
            if (outcome == null)
            {
                _resultCard.SetStatus(MolcaStatusKind.None, _wasCancelled ? "Cancelled" : "No result");
                _resultBody.Add(NetworkHubUi.Note(_wasCancelled
                    ? "The send was cancelled. Nothing was retained."
                    : "Send a request to see status, timings, attempts, and a redacted body."));
                return;
            }

            _resultCard.SetStatus(
                outcome.IsSuccess ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                $"{outcome.StatusCode} {(outcome.IsSuccess ? "OK" : outcome.Category.ToString())}");

            _resultBody.Add(NetworkHubUi.Field("Status", $"{outcome.StatusCode} · {outcome.Category}"));
            _resultBody.Add(NetworkHubUi.Field("Final URI",
                Molca.Networking.Utils.LogRedaction.RedactUrl(outcome.Uri)));
            _resultBody.Add(NetworkHubUi.Field("Correlation", outcome.CorrelationId,
                "Matches this send to its diagnostic record and to any log line the pipeline wrote."));

            if (!outcome.IsSuccess && !string.IsNullOrEmpty(outcome.Message))
                _resultBody.Add(NetworkHubUi.Field("Error", outcome.Message));

            _resultBody.Add(NetworkHubUi.Heading("Timing"));
            var timings = outcome.Timings;
            _resultBody.Add(NetworkHubUi.Field("Total", Ms(timings.Total)));
            _resultBody.Add(NetworkHubUi.Field("Queue", Ms(timings.Queued)));
            _resultBody.Add(NetworkHubUi.Field("Authentication", Ms(timings.Authentication)));
            _resultBody.Add(NetworkHubUi.Field("Retry delay", Ms(timings.RetryDelay)));
            _resultBody.Add(NetworkHubUi.Field("Wire", Ms(timings.Wire)));

            _resultBody.Add(NetworkHubUi.Heading($"Attempts ({outcome.AttemptCount})"));
            foreach (var attempt in outcome.Attempts)
            {
                _resultBody.Add(NetworkHubUi.Field(
                    $"#{attempt.Attempt}",
                    $"{attempt.StatusCode} {attempt.Category} · {Ms(attempt.Duration)}" +
                    (attempt.DelayBefore > TimeSpan.Zero ? $" after {Ms(attempt.DelayBefore)}" : string.Empty) +
                    (attempt.Authenticated ? " · authenticated" : string.Empty),
                    string.IsNullOrEmpty(attempt.Message) ? null : attempt.Message));
            }

            _resultBody.Add(NetworkHubUi.Field("Served from cache", outcome.ServedFromCache ? "yes" : "no"));
            _resultBody.Add(NetworkHubUi.Field("Redirects followed", outcome.RedirectCount.ToString()));

            if (outcome.SecurityClamps != null && outcome.SecurityClamps.Count > 0)
            {
                _resultBody.Add(NetworkHubUi.Heading("Security clamps"));
                foreach (string clamp in outcome.SecurityClamps)
                    _resultBody.Add(NetworkHubUi.Note(clamp));
            }

            _resultBody.Add(NetworkHubUi.Heading("Response headers"));
            _resultBody.Add(BuildHeaders(outcome));

            _resultBody.Add(NetworkHubUi.Heading("Body"));
            string body = _session.Console.LastBodyPreview;
            if (string.IsNullOrEmpty(body))
            {
                _resultBody.Add(NetworkHubUi.Note("No body, or the response was not text."));
            }
            else
            {
                var preview = new TextField { value = body, multiline = true, isReadOnly = true };
                preview.AddToClassList("molca-network__code");
                _resultBody.Add(preview);
            }

            _resultBody.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Copy diagnostics", () => CopyDiagnostics(outcome))));
        }

        /// <summary>
        /// Response headers, with credential-shaped values masked.
        /// </summary>
        /// <param name="outcome">The outcome whose headers to render.</param>
        /// <remarks>
        /// <c>Set-Cookie</c> is the reason this is not a plain dump: a session cookie in a response is a
        /// credential, and the console is a surface people screenshot.
        /// </remarks>
        private static VisualElement BuildHeaders(RoutedHttpOutcome outcome)
        {
            var container = new VisualElement();

            if (outcome.Headers == null || outcome.Headers.Count == 0)
            {
                container.Add(NetworkHubUi.Note("No headers."));
                return container;
            }

            foreach (var header in outcome.Headers)
            {
                container.Add(NetworkHubUi.Field(
                    header.Key,
                    Molca.Networking.Utils.LogRedaction.RedactHeaderValue(header.Key, header.Value)));
            }

            return container;
        }

        private static void CopyDiagnostics(RoutedHttpOutcome outcome)
        {
            EditorGUIUtility.systemCopyBuffer = outcome.ToRedactedString();
        }

        #endregion

        #region History

        private VisualElement BuildHistory()
        {
            var card = NetworkHubUi.Card("History", $"Redacted · newest first · at most {NetworkConsoleRunner.HistoryCapacity}");
            card.Body.Add(_historyBody);
            return card;
        }

        private void RefreshHistory()
        {
            _historyBody.Clear();

            var diagnostics = _session.Console.Diagnostics;
            var records = diagnostics?.Snapshot();

            if (records == null || records.Count == 0)
            {
                _historyBody.Add(NetworkHubUi.Note(
                    "Nothing sent from the console yet. History holds redacted records only — no headers, " +
                    "no credential values — and is discarded on a domain reload."));
                return;
            }

            for (int i = records.Count - 1; i >= 0; i--)
            {
                var record = records[i];
                _historyBody.Add(NetworkHubUi.ListRow(
                    $"{record.Method} {Molca.Networking.Utils.LogRedaction.RedactUrl(record.Uri)}",
                    $"{record.Route} · {record.CompletedUtc:HH:mm:ss} · {record.Timings.Total.TotalMilliseconds:F0} ms",
                    record.IsSuccess ? MolcaStatusKind.Ok : MolcaStatusKind.Error,
                    record.IsSuccess ? "Succeeded" : record.Category.ToString(),
                    selected: false,
                    onClick: null,
                    NetworkHubUi.Badge(record.StatusCode > 0 ? record.StatusCode.ToString() : "—")));
            }

            _historyBody.Add(NetworkHubUi.Actions(
                MolcaButtons.Mini("Copy history", () =>
                    EditorGUIUtility.systemCopyBuffer = _session.Console.ExportHistory()),
                MolcaButtons.Mini("Clear", () => _session.Console.ClearHistory())));
        }

        #endregion

        #region Helpers

        private static string Ms(TimeSpan span) => $"{span.TotalMilliseconds:F0} ms";

        private static List<string> Ids<T>(IReadOnlyList<T> source, Func<T, string> select)
        {
            var ids = new List<string>();
            if (source == null) return ids;

            foreach (var item in source)
            {
                string id = select(item);
                if (!string.IsNullOrEmpty(id)) ids.Add(id);
            }
            return ids;
        }

        private static List<NetworkEndpointDefinition> EndpointsOf(NetworkServiceDefinition service)
        {
            var endpoints = new List<NetworkEndpointDefinition>();
            if (service?.EndpointCollections == null) return endpoints;

            foreach (var collection in service.EndpointCollections)
            {
                if (collection?.Endpoints == null) continue;

                foreach (var endpoint in collection.Endpoints)
                {
                    if (endpoint != null && !string.IsNullOrEmpty(endpoint.Id) &&
                        endpoint.RequiredProtocol == NetworkProtocols.Http)
                    {
                        endpoints.Add(endpoint);
                    }
                }
            }
            return endpoints;
        }

        private static NetworkEndpointDefinition Find(List<NetworkEndpointDefinition> endpoints, string id)
        {
            foreach (var endpoint in endpoints)
            {
                if (string.Equals(endpoint.Id, id, StringComparison.Ordinal)) return endpoint;
            }
            return null;
        }

        private static VisualElement Popup(
            string label, List<string> choices, string current, Action<string> onChanged)
        {
            if (choices.Count == 0)
                return NetworkHubUi.Field(label, null);

            string value = choices.Contains(current) ? current : choices[0];
            var field = new PopupField<string>(label, choices, value);
            field.RegisterValueChangedCallback(evt => onChanged(evt.newValue));

            // A popup built with a value the draft did not have must write it back, or the draft and the
            // control disagree until the user touches it.
            if (!string.Equals(value, current, StringComparison.Ordinal))
                onChanged(value);

            return field;
        }

        /// <summary>
        /// An editable list of key/value entries with add and remove.
        /// </summary>
        /// <param name="entries">The list to edit in place.</param>
        /// <param name="keyPlaceholder">Placeholder for the key column.</param>
        /// <param name="valuePlaceholder">Placeholder for the value column.</param>
        /// <remarks>
        /// Values commit on blur rather than on every keystroke. These are plain in-memory fields with no
        /// Undo behind them, but per-keystroke commits would still rebuild the preflight on every
        /// character typed into a body or a token.
        /// </remarks>
        private VisualElement EntryList(
            List<NetworkConsoleRequest.Entry> entries, string keyPlaceholder, string valuePlaceholder)
        {
            var container = new VisualElement();

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                int index = i;

                var row = new VisualElement();
                row.AddToClassList("molca-network__entry-row");

                var enabled = new Toggle { value = entry.Enabled };
                enabled.RegisterValueChangedCallback(evt =>
                {
                    entry.Enabled = evt.newValue;
                    RefreshPreflight();
                });
                row.Add(enabled);

                var key = new TextField { value = entry.Key };
                key.AddToClassList("molca-network__entry-key");
                key.RegisterCallback<BlurEvent>(_ =>
                {
                    entry.Key = key.value;
                    RefreshPreflight();
                });
                row.Add(key);

                var value = new TextField { value = entry.Value };
                value.AddToClassList("molca-network__entry-value");
                value.RegisterCallback<BlurEvent>(_ =>
                {
                    entry.Value = value.value;
                    RefreshPreflight();
                });
                row.Add(value);

                row.Add(MolcaButtons.Mini("−", () =>
                {
                    entries.RemoveAt(index);
                    OnDraftChanged();
                }));

                container.Add(row);
            }

            container.Add(MolcaButtons.Mini("+ Add", () =>
            {
                entries.Add(new NetworkConsoleRequest.Entry());
                OnDraftChanged();
            }));

            return container;
        }

        #endregion
    }
}
