#!/bin/bash
# 通过 env 注入 WorkBuddy bash 运行时缺失的系统环境变量后运行 dotnet。
# 缺少 ProgramData/APPDATA/ProgramFiles 系列时，.NET 8 的 Environment.GetFolderPath
# 返回空值，NuGet 的 XPlatMachineWideSetting 会以 ArgumentNullException 崩溃。
# 注意：bash 的 export 不接受带括号的变量名，必须用 env 命令注入。
# 用法：./dotnet-env.sh <任意 dotnet 参数>
exec env -u HTTP_PROXY -u HTTPS_PROXY -u http_proxy -u https_proxy -u ALL_PROXY -u all_proxy \
  ProgramData='C:\ProgramData' \
  ALLUSERSPROFILE='C:\ProgramData' \
  APPDATA='C:\Users\31571\AppData\Roaming' \
  ProgramFiles='C:\Program Files' \
  'ProgramFiles(x86)'='C:\Program Files (x86)' \
  ProgramW6432='C:\Program Files' \
  CommonProgramFiles='C:\Program Files\Common Files' \
  'CommonProgramFiles(x86)'='C:\Program Files (x86)\Common Files' \
  CommonProgramW6432='C:\Program Files\Common Files' \
  DOTNET_ROOT='C:\Users\31571\.dotnet' \
  DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  DOTNET_NOLOGO=1 \
  /c/Users/31571/.dotnet/dotnet.exe "$@"
