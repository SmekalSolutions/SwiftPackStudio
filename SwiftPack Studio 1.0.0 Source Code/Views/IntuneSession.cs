using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace SwiftPack.Views
{
    /// <summary>One Win32 app returned from Intune (for the dependency / supersedence pickers).</summary>
    public sealed class IntuneAppItem
    {
        public string Id = "", Name = "", Publisher = "", Version = "";
        public int DependentCount;
        public int SupersededCount;
    }

    /// <summary>One Entra (Azure AD) group returned from Graph (for the group picker).</summary>
    public sealed class EntraGroup
    {
        public string Id = "", Name = "", Mail = "";
    }

    // ── Persistent PowerShell session (no-secret, no-relogin publishing) ─────────────
    // A delegated sign-in token is bound to the process that acquired it and cannot be
    // replayed elsewhere. So we keep one powershell.exe alive: Connect-MSIntuneGraph runs
    // once, and every publish / Graph query runs inside this same session, reusing that
    // in-process connection without prompting again.
    public sealed class PsSession : IDisposable
    {
        private System.Diagnostics.Process? _proc;
        private TaskCompletionSource<int>? _pending;
        private string _doneId = "";
        private Action<string>? _log;

        public bool IsAlive => _proc != null && !_proc.HasExited;

        public bool Start()
        {
            if (IsAlive) return true;
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = "-NoProfile -NoLogo -ExecutionPolicy Bypass",
                UseShellExecute        = false,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
            };
            _proc = System.Diagnostics.Process.Start(psi);
            if (_proc == null) return false;
            _proc.EnableRaisingEvents = true;
            _proc.Exited += (_, _) =>
            {
                // If the process dies (killed on dispose, or crashes) while a command is
                // pending, complete the awaiter now instead of waiting for the timeout.
                var p = _pending; _pending = null;
                p?.TrySetResult(-1);
            };
            _proc.OutputDataReceived += OnData;
            _proc.ErrorDataReceived  += OnData;
            _proc.BeginOutputReadLine();
            _proc.BeginErrorReadLine();
            _proc.StandardInput.WriteLine("function prompt { '' }; $ProgressPreference='SilentlyContinue'; $ErrorActionPreference='Continue'");
            _proc.StandardInput.Flush();
            return true;
        }

        private void OnData(object sender, System.Diagnostics.DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            int idx = e.Data.IndexOf("##PSEXIT##", StringComparison.Ordinal);
            if (idx >= 0)
            {
                string rest = e.Data.Substring(idx + "##PSEXIT##".Length);
                var parts = rest.Split(new[] { "##" }, StringSplitOptions.None);
                if (parts.Length >= 2 && parts[0] == _doneId)
                {
                    int code = int.TryParse(parts[1].Trim(), out int c) ? c : -1;
                    var p = _pending; _pending = null;
                    p?.TrySetResult(code);
                    return;
                }
            }
            _log?.Invoke(e.Data);
        }

        // Runs one command, forwards its output to log, and returns its $LASTEXITCODE.
        public async Task<int> RunAsync(string commandLine, Action<string> log, int timeoutMs)
        {
            if (!IsAlive) throw new InvalidOperationException("The PowerShell session is not running.");
            _log = log;
            _doneId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<int>();
            _pending = tcs;
            _proc!.StandardInput.WriteLine(commandLine);
            _proc.StandardInput.WriteLine($"Write-Output \"##PSEXIT##{_doneId}##$LASTEXITCODE\"");
            _proc.StandardInput.Flush();
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done != tcs.Task)
            {
                _pending = null;
                if (!IsAlive) throw new InvalidOperationException("The PowerShell session ended unexpectedly.");
                throw new TimeoutException("The PowerShell command timed out.");
            }
            return await tcs.Task;
        }

        public void Dispose()
        {
            try
            {
                if (IsAlive)
                {
                    _proc!.StandardInput.Close();
                    if (!_proc.WaitForExit(2000)) _proc.Kill(true);
                }
            }
            catch { }
            try { _proc?.Dispose(); } catch { }
            _proc = null;
        }
    }

    /// <summary>
    /// App-wide Intune sign-in session. Holds one persistent PowerShell connection so the
    /// user signs in once (via the top-bar "Sign in to Intune" button) and every consumer -
    /// the Publish page's publish flow and the Auto Package pickers - reuses it without a
    /// second browser prompt. This is deliberately self-contained (no page dependency).
    /// </summary>
    public static class IntuneSession
    {
        private static PsSession? _session;
        private static bool _connected;

        /// <summary>The live session (null when signed out). Callers run publish / query
        /// commands through this.</summary>
        public static PsSession? Session => _session;

        /// <summary>True only when a session exists, is alive, and Connect-MSIntuneGraph succeeded.</summary>
        public static bool IsConnected => _connected && _session != null && _session.IsAlive;

        /// <summary>Raised whenever the connected state changes, so any UI (e.g. the top-bar
        /// button) can reflect it. Fires with the current <see cref="IsConnected"/> value.</summary>
        public static event Action<bool>? ConnectedChanged;

        /// <summary>Disposes any existing session and starts a fresh (not-yet-connected) one.
        /// The caller runs Connect-MSIntuneGraph on it, then calls <see cref="SetConnected"/>.</summary>
        public static PsSession StartFresh()
        {
            try { _session?.Dispose(); } catch { }
            _session = new PsSession();
            _connected = false;
            return _session;
        }

        /// <summary>Marks the current session as connected or not, and notifies listeners.</summary>
        public static void SetConnected(bool connected)
        {
            _connected = connected;
            ConnectedChanged?.Invoke(IsConnected);
        }

        /// <summary>Signs out: disposes the session and notifies listeners.</summary>
        public static void SignOut()
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            _connected = false;
            ConnectedChanged?.Invoke(false);
        }

        // Lists Win32 apps from Intune via the signed-in session. Returns null on error.
        public static async Task<List<IntuneAppItem>?> FetchAppsAsync(Action<string>? log = null)
        {
            if (_session == null || !_session.IsAlive) return null;
            string cmd =
                "try { " +
                "$h=@{ Authorization = $Global:AuthenticationHeader['Authorization'] }; " +
                "$u='https://graph.microsoft.com/beta/deviceAppManagement/mobileApps?$top=100'; " +
                "$all=@(); " +
                "do { $r=Invoke-RestMethod -Uri $u -Headers $h -Method GET -ErrorAction Stop; $all+=$r.value; $u=$r.'@odata.nextLink' } while ($u); " +
                "$o=@($all | Where-Object { $_.'@odata.type' -eq '#microsoft.graph.win32LobApp' } | ForEach-Object { [pscustomobject]@{ id=$_.id; name=$_.displayName; publisher=$_.publisher; version=$_.displayVersion; depCount=$_.dependentAppCount; supCount=$_.supersededAppCount } }); " +
                "Write-Output ('##APPS##' + ($o | ConvertTo-Json -Compress -Depth 4) + '##APPSEND##') " +
                "} catch { Write-Output ('##APPSERR##' + $_.Exception.Message) }";

            string payload = "";
            string err = "";
            try
            {
                await _session.RunAsync(cmd, line =>
                {
                    int a = line.IndexOf("##APPS##", StringComparison.Ordinal);
                    int b = line.IndexOf("##APPSEND##", StringComparison.Ordinal);
                    if (a >= 0 && b > a) { payload = line.Substring(a + 8, b - (a + 8)); return; }
                    int eIdx = line.IndexOf("##APPSERR##", StringComparison.Ordinal);
                    if (eIdx >= 0) { err = line.Substring(eIdx + "##APPSERR##".Length); return; }
                }, 120000);
            }
            catch (Exception ex) { log?.Invoke("Load apps error: " + ex.Message); return null; }

            if (!string.IsNullOrEmpty(err)) { log?.Invoke("Load apps: " + err); return null; }

            var list = new List<IntuneAppItem>();
            if (string.IsNullOrWhiteSpace(payload)) return list;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray()) list.Add(ParseIntuneApp(el));
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    list.Add(ParseIntuneApp(doc.RootElement));
                list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
            catch (Exception ex) { log?.Invoke("Load apps parse error: " + ex.Message); return null; }
        }

        private static IntuneAppItem ParseIntuneApp(JsonElement el)
        {
            string S(string k) => el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
            int I(string k) => el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : 0;
            return new IntuneAppItem { Id = S("id"), Name = S("name"), Publisher = S("publisher"), Version = S("version"), DependentCount = I("depCount"), SupersededCount = I("supCount") };
        }

        // Lists Entra (Azure AD) groups via the signed-in session. Returns null on error.
        public static async Task<List<EntraGroup>?> FetchGroupsAsync(Action<string>? log = null)
        {
            if (_session == null || !_session.IsAlive) return null;
            string cmd =
                "try { " +
                "$h=@{ Authorization = $Global:AuthenticationHeader['Authorization'] }; " +
                "$u='https://graph.microsoft.com/v1.0/groups?$select=id,displayName,mail&$top=200'; " +
                "$all=@(); " +
                "do { $r=Invoke-RestMethod -Uri $u -Headers $h -Method GET -ErrorAction Stop; $all+=$r.value; $u=$r.'@odata.nextLink' } while ($u); " +
                "$o=@($all | ForEach-Object { [pscustomobject]@{ id=$_.id; name=$_.displayName; mail=$_.mail } }); " +
                "Write-Output ('##GROUPS##' + ($o | ConvertTo-Json -Compress -Depth 4) + '##GROUPSEND##') " +
                "} catch { Write-Output ('##GROUPSERR##' + $_.Exception.Message) }";

            string payload = "";
            string err = "";
            try
            {
                await _session.RunAsync(cmd, line =>
                {
                    int a = line.IndexOf("##GROUPS##", StringComparison.Ordinal);
                    int b = line.IndexOf("##GROUPSEND##", StringComparison.Ordinal);
                    if (a >= 0 && b > a) { payload = line.Substring(a + "##GROUPS##".Length, b - (a + "##GROUPS##".Length)); return; }
                    int eIdx = line.IndexOf("##GROUPSERR##", StringComparison.Ordinal);
                    if (eIdx >= 0) { err = line.Substring(eIdx + "##GROUPSERR##".Length); return; }
                }, 120000);
            }
            catch (Exception ex) { log?.Invoke("Load groups error: " + ex.Message); return null; }

            if (!string.IsNullOrEmpty(err)) { log?.Invoke("Load groups: " + err); return null; }

            var list = new List<EntraGroup>();
            if (string.IsNullOrWhiteSpace(payload)) return list;
            try
            {
                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var el in doc.RootElement.EnumerateArray()) list.Add(ParseEntraGroup(el));
                else if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    list.Add(ParseEntraGroup(doc.RootElement));
                list.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase));
                return list;
            }
            catch (Exception ex) { log?.Invoke("Load groups parse error: " + ex.Message); return null; }
        }

        private static EntraGroup ParseEntraGroup(JsonElement el)
        {
            string S(string k) => el.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
            return new EntraGroup { Id = S("id"), Name = S("name"), Mail = S("mail") };
        }
    }
}
