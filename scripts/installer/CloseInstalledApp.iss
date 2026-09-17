[Code]
const
  RichChatProcessAccess = $00100000 or $00001000 or $00000001;
  RichChatWaitFinished = 0;

function OpenProcess(Access: LongWord; InheritHandle: Boolean; ProcessId: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function QueryFullProcessImageName(Handle: THandle; Flags: LongWord; ImageName: String; var Size: LongWord): Boolean;
  external 'QueryFullProcessImageNameW@kernel32.dll stdcall';
function TerminateProcess(Handle: THandle; ExitCode: LongWord): Boolean;
  external 'TerminateProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';

function HasCloseSwitch(Value: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Value) = 0 then Result := True;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Locator, Services, Processes, Process: Variant;
  I: Integer;
  Target, ImageName: String;
  ImageLength: LongWord;
  Handle: THandle;
  Approved: Boolean;
begin
  Result := '';
  Approved := False;
  Target := ExpandFileName(ExpandConstant('{app}\RichChat.exe'));
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Services := Locator.ConnectServer('', 'root\CIMV2');
    Processes := Services.ExecQuery('SELECT ProcessId FROM Win32_Process WHERE Name = ''RichChat.exe''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      Handle := OpenProcess(RichChatProcessAccess, False, Process.ProcessId);
      if Handle <> 0 then
      begin
        try
          ImageLength := 32768;
          SetLength(ImageName, ImageLength);
          if QueryFullProcessImageName(Handle, 0, ImageName, ImageLength) then
          begin
            SetLength(ImageName, ImageLength);
            if CompareText(ExpandFileName(ImageName), Target) = 0 then
            begin
              if not Approved then
              begin
                if HasCloseSwitch('/NOCLOSEAPPLICATIONS') then
                  Approved := False
                else if WizardSilent then
                  Approved := HasCloseSwitch('/CLOSEAPPLICATIONS')
                else
                  Approved := MsgBox('更新需要退出此安装目录中正在运行的 RichChat。' + #13#10 +
                    '请先保存未发送的内容。选择“是”将结束该进程并继续安装，选择“否”可返回后手动从托盘退出。' + #13#10 + #13#10 +
                    Target, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
                if not Approved then
                begin
                  Result := '请从右下角托盘退出此安装目录中的 RichChat，然后重试。';
                  Exit;
                end;
              end;
              // The retained handle identifies this exact process even if its PID is reused.
              if not TerminateProcess(Handle, 0) then
              begin
                if WaitForSingleObject(Handle, 0) <> RichChatWaitFinished then
                begin
                  Result := '无法退出 RichChat，请从托盘手动退出后重试。';
                  Exit;
                end;
              end;
              if WaitForSingleObject(Handle, 5000) <> RichChatWaitFinished then
              begin
                Result := 'RichChat 尚未退出，请从托盘手动退出后重试。';
                Exit;
              end;
            end;
          end
          else if WaitForSingleObject(Handle, 0) <> RichChatWaitFinished then
          begin
            Result := '无法确认 RichChat 进程所在目录，请手动退出 RichChat 后重试安装。';
            Exit;
          end;
        finally
          CloseHandle(Handle);
        end;
      end
      else
      begin
        Result := '无法访问 RichChat 进程，请手动退出 RichChat 后重试安装。';
        Exit;
      end;
    end;
  except
    Result := '无法检查正在运行的 RichChat，请关闭应用后重试安装。';
  end;
end;
