; grok-mcp installer - Inno Setup script
;
; Build: ISCC.exe installer\grok-mcp.iss
;        Override version: ISCC.exe /DAppVersion=1.0.1 installer\grok-mcp.iss
;
; Inputs (must exist before ISCC runs):
;   - bin/win-x64/grok-mcp.exe   (from `dotnet publish`)
;
; Output:
;   - bin/installer/GrokMcpSetup-<version>-win-x64.exe

#ifndef AppVersion
  #define AppVersion "1.2.0"
#endif

#define TaskName "grok-mcp-server"
#define McpPort "6677"
#define McpUrl "http://127.0.0.1:" + McpPort + "/mcp"

[Setup]
AppId={{7B5E2C8A-9F4D-4B1E-A6C3-7B2D1E3F4A50}
AppName=grok-mcp
AppVersion={#AppVersion}
AppPublisher=SACRVM
AppPublisherURL=https://github.com/SACRVM/grok-mcp
AppSupportURL=https://github.com/SACRVM/grok-mcp/issues
AppUpdatesURL=https://github.com/SACRVM/grok-mcp/releases
DefaultDirName={localappdata}\Programs\grok-mcp
DefaultGroupName=grok-mcp
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=..\bin\installer
OutputBaseFilename=GrokMcpSetup-{#AppVersion}-win-x64
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\grok-mcp.exe
UninstallDisplayName=grok-mcp {#AppVersion}
WizardImageStretch=no
ShowLanguageDialog=no
DisableWelcomePage=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\bin\win-x64\grok-mcp.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "install-task.ps1";           DestDir: "{app}"; Flags: ignoreversion
Source: "uninstall-task.ps1";         DestDir: "{app}"; Flags: ignoreversion

[Run]
; Register + start the Scheduled Task (on-logon, auto-restart on crash).
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\install-task.ps1"" -ExePath ""{app}\grok-mcp.exe"""; \
  StatusMsg: "Registering background service..."; \
  Flags: runhidden waituntilterminated

[UninstallRun]
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File ""{app}\uninstall-task.ps1"""; \
  RunOnceId: "RemoveScheduledTask"; \
  Flags: runhidden

[UninstallDelete]
; Logs and config.env in %LOCALAPPDATA%\grok-mcp\ are kept on uninstall by design.
; Users who want a full wipe can delete that folder manually.

[Code]
var
  KeyPage: TInputQueryWizardPage;
  // Set true when the [Code] flow successfully registered the MCP with Claude Code,
  // so the final dialog can confirm it.
  ClaudeRegistered: Boolean;
  ClaudeCliFound: Boolean;

function CRLF(): String;
begin
  Result := Chr(13) + Chr(10);
end;

function ConfigEnvPath(): String;
begin
  Result := ExpandConstant('{localappdata}\grok-mcp\config.env');
end;

// Split a string on LF, dropping any trailing CR per line.
procedure SplitLines(const text: String; var lines: TArrayOfString);
var
  p, i: Integer;
  chunk: String;
begin
  p := 1;
  SetArrayLength(lines, 0);
  while p <= Length(text) do
  begin
    i := Pos(Chr(10), Copy(text, p, Length(text)));
    SetArrayLength(lines, GetArrayLength(lines) + 1);
    if i = 0 then
    begin
      chunk := Copy(text, p, Length(text));
      if (Length(chunk) > 0) and (chunk[Length(chunk)] = Chr(13)) then
        chunk := Copy(chunk, 1, Length(chunk) - 1);
      lines[GetArrayLength(lines) - 1] := chunk;
      Break;
    end;
    chunk := Copy(text, p, i - 1);
    if (Length(chunk) > 0) and (chunk[Length(chunk)] = Chr(13)) then
      chunk := Copy(chunk, 1, Length(chunk) - 1);
    lines[GetArrayLength(lines) - 1] := chunk;
    p := p + i;
  end;
end;

// Returns the value after "XAI_API_KEY=" in the current config.env, or '' if
// the file is missing or the key line is blank.
function ReadApiKey(): String;
var
  raw: AnsiString;
  i: Integer;
  lines: TArrayOfString;
  line: String;
begin
  Result := '';
  if not FileExists(ConfigEnvPath()) then Exit;
  if not LoadStringFromFile(ConfigEnvPath(), raw) then Exit;
  SplitLines(String(raw), lines);
  for i := 0 to GetArrayLength(lines) - 1 do
  begin
    line := Trim(lines[i]);
    if Copy(line, 1, 12) = 'XAI_API_KEY=' then
    begin
      Result := Trim(Copy(line, 13, Length(line)));
      Exit;
    end;
  end;
end;

function ApiKeyIsMissing(): Boolean;
begin
  Result := ReadApiKey() = '';
end;

function GetEnteredKey(): String;
begin
  if KeyPage = nil then
    Result := ''
  else
    Result := Trim(KeyPage.Values[0]);
end;

// Update only the XAI_API_KEY= line in an existing config.env, preserving every
// other line (comments, optional overrides the user customized, etc.).
// If the line doesn't exist, append one.
procedure UpdateApiKeyInPlace(const newKey: String);
var
  raw: AnsiString;
  i: Integer;
  lines: TArrayOfString;
  line, trimmed, buf, nl: String;
  replaced: Boolean;
begin
  if not LoadStringFromFile(ConfigEnvPath(), raw) then Exit;
  SplitLines(String(raw), lines);
  nl := CRLF();
  buf := '';
  replaced := False;

  for i := 0 to GetArrayLength(lines) - 1 do
  begin
    line := lines[i];
    trimmed := Trim(line);
    if (not replaced) and (Copy(trimmed, 1, 12) = 'XAI_API_KEY=') then
    begin
      buf := buf + 'XAI_API_KEY=' + newKey;
      replaced := True;
    end
    else
      buf := buf + line;
    buf := buf + nl;
  end;

  if not replaced then
    buf := buf + 'XAI_API_KEY=' + newKey + nl;

  SaveStringToFile(ConfigEnvPath(), buf, False);
end;

// Write a fresh config.env template with the supplied key inlined.
procedure WriteFreshConfigEnv(const newKey: String);
var
  body, nl: String;
begin
  ForceDirectories(ExpandConstant('{localappdata}\grok-mcp'));
  nl := CRLF();
  body :=
    '# grok-mcp config - read at server startup.' + nl +
    '# %LOCALAPPDATA%\grok-mcp\config.env takes precedence over' + nl +
    '# %USERPROFILE%\.grok-mcp\config.env. Process env vars override both.' + nl +
    nl +
    '# REQUIRED - your xAI key (no quotes, no spaces):' + nl +
    'XAI_API_KEY=' + newKey + nl +
    nl +
    '# Optional model overrides (defaults shown):' + nl +
    '# GROK_MCP_CHAT_MODEL=grok-4.7' + nl +
    '# GROK_MCP_CREATIVE_MODEL=grok-4.7' + nl +
    '# grok_chat_fast needs a model that cannot reason:' + nl +
    '# GROK_MCP_FAST_MODEL=grok-4.20-0309-non-reasoning' + nl +
    '# GROK_MCP_MULTI_AGENT_MODEL=grok-4.20-multi-agent-0309' + nl +
    '# GROK_MCP_IMAGE_MODEL=grok-imagine-image-2.0' + nl +
    '# One video model for text-to-video and image-to-video:' + nl +
    '# GROK_MCP_VIDEO_MODEL=grok-imagine-video-1.5' + nl +
    nl +
    '# Optional runtime tuning:' + nl +
    '# GROK_MCP_LOG_LEVEL=Information' + nl +
    '# GROK_MCP_HTTP_TIMEOUT_SEC=3600' + nl +
    '# GROK_MCP_SESSION_CAP=50' + nl;
  SaveStringToFile(ConfigEnvPath(), body, False);
end;

// Persist whatever the user typed. Three cases:
//   1. The user left the field blank   -> do nothing (keep whatever was there).
//   2. The file doesn't exist yet      -> write full template with the entered key.
//   3. The file exists                 -> in-place update of the key line only,
//                                         preserving the user's other settings.
procedure PersistKeyFromWizard();
var
  entered: String;
begin
  entered := GetEnteredKey();
  if entered = '' then Exit;

  if not FileExists(ConfigEnvPath()) then
    WriteFreshConfigEnv(entered)
  else
    UpdateApiKeyInPlace(entered);
end;

procedure RestartScheduledTask();
var
  resultCode: Integer;
begin
  Exec('powershell.exe',
    '-NoProfile -WindowStyle Hidden -Command "Stop-ScheduledTask -TaskName {#TaskName} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 1; Start-ScheduledTask -TaskName {#TaskName}"',
    '', SW_HIDE, ewWaitUntilTerminated, resultCode);
end;

function ClaudeCliAvailable(): Boolean;
var
  resultCode: Integer;
begin
  Result := Exec('cmd.exe', '/c where claude >nul 2>&1', '',
    SW_HIDE, ewWaitUntilTerminated, resultCode) and (resultCode = 0);
end;

// Idempotent: removes any prior user-scope `grok` registration first, then adds
// the HTTP-transport URL. Both calls swallow stdout/stderr; we only care about
// the exit code of the add step.
function RegisterMcpInClaudeCode(): Boolean;
var
  resultCode: Integer;
begin
  Result := False;
  if not ClaudeCliAvailable() then Exit;

  Exec('cmd.exe',
    '/c claude mcp remove grok --scope user >nul 2>&1',
    '', SW_HIDE, ewWaitUntilTerminated, resultCode);

  if Exec('cmd.exe',
       '/c claude mcp add grok --scope user --transport http {#McpUrl} >nul 2>&1',
       '', SW_HIDE, ewWaitUntilTerminated, resultCode) then
    Result := (resultCode = 0);
end;

procedure InitializeWizard;
var
  existing: String;
  subtitle: String;
begin
  existing := ReadApiKey();
  if existing = '' then
    subtitle := 'Paste your xAI API key'
  else
    subtitle := 'Review or change your xAI API key';

  KeyPage := CreateInputQueryPage(wpSelectDir,
    'xAI API Key',
    subtitle,
    'Get a key from https://console.x.ai/ . The key is stored locally at ' +
    '%LOCALAPPDATA%\grok-mcp\config.env and never logged. ' +
    'Leave the current value to keep it; clear it to skip and edit the file manually later.');
  KeyPage.Add('API key (xai-...):', False);
  KeyPage.Values[0] := existing;
end;

// Light sanity check on the entered key. The user can still proceed past warnings.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  key: String;
begin
  Result := True;
  if (KeyPage <> nil) and (CurPageID = KeyPage.ID) then
  begin
    key := GetEnteredKey();
    if (key <> '') and (Copy(key, 1, 4) <> 'xai-') then
    begin
      if MsgBox('That key does not start with "xai-" (xAI keys usually do). Continue anyway?',
                mbConfirmation, MB_YESNO) = IDNO then
        Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    PersistKeyFromWizard();
    // If we now have a key (entered just now, or already there from a prior
    // install), bounce the freshly-installed service so it reloads config.env.
    if not ApiKeyIsMissing() then
      RestartScheduledTask();
    ClaudeCliFound := ClaudeCliAvailable();
    if ClaudeCliFound and (not ApiKeyIsMissing()) then
      ClaudeRegistered := RegisterMcpInClaudeCode();
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  cfg, msg: String;
  resultCode: Integer;
begin
  if CurPageID <> wpFinished then Exit;

  cfg := ConfigEnvPath();

  if ApiKeyIsMissing() then
  begin
    msg :=
      'Installation complete - but the server cannot start yet because no API key was provided.' + CRLF() + CRLF() +
      'Open the config file shown next and replace the empty XAI_API_KEY= line' + CRLF() +
      'with your real key from https://console.x.ai/' + CRLF() + CRLF() +
      'Example:' + CRLF() +
      '    XAI_API_KEY=xai-abc123...' + CRLF() + CRLF() +
      'After saving, run these two lines in PowerShell:' + CRLF() + CRLF() +
      '    Stop-ScheduledTask  -TaskName {#TaskName}' + CRLF() +
      '    Start-ScheduledTask -TaskName {#TaskName}' + CRLF() + CRLF() +
      'And register with Claude Code:' + CRLF() +
      '    claude mcp add grok --scope user --transport http {#McpUrl}' + CRLF() + CRLF() +
      'Config file: ' + cfg;
    MsgBox(msg, mbInformation, MB_OK);
    ShellExec('open', 'notepad.exe', '"' + cfg + '"', '', SW_SHOW, ewNoWait, resultCode);
  end
  else
  begin
    msg := 'grok-mcp is ready to use!' + CRLF() + CRLF() +
           '  Server:  {#McpUrl}  (Scheduled Task: {#TaskName})' + CRLF();
    if ClaudeRegistered then
      msg := msg + '  Claude:  registered as user-scope MCP "grok"' + CRLF() + CRLF() +
             'Open any Claude Code session and try:' + CRLF() +
             '    "Use grok_chat to summarize what''s new with Grok."'
    else if not ClaudeCliFound then
      msg := msg + '  Claude:  CLI not found in PATH - register manually:' + CRLF() +
             '           claude mcp add grok --scope user --transport http {#McpUrl}'
    else
      msg := msg + '  Claude:  registration failed - run manually:' + CRLF() +
             '           claude mcp add grok --scope user --transport http {#McpUrl}';
    MsgBox(msg, mbInformation, MB_OK);
  end;
end;
