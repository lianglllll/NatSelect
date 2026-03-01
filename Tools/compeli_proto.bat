@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo.
echo ========================================
echo   Proto 编译脚本 (C#) - 详细调试模式
echo   脚本位置: %~dp0
echo ========================================
echo.

:: ========== 1. 基础路径构建 ==========
set "SCRIPT_DIR=%~dp0"
set "SCRIPT_DIR=%SCRIPT_DIR:~0,-1%"
set "RAW_SOURCE=%SCRIPT_DIR%\..\NatSelect\Network\Proto\Source"
set "RAW_TARGET=%SCRIPT_DIR%\..\NatSelect\Network\Proto\Target"
set "RAW_PROTOC=%SCRIPT_DIR%\protoc-34.0-win64\bin\protoc.exe"

echo [步骤1] 原始路径:
echo   RAW_SOURCE = %RAW_SOURCE%
echo   RAW_TARGET = %RAW_TARGET%
echo   RAW_PROTOC = %RAW_PROTOC%
echo.

:: ========== 2. 严格验证源目录 ==========
if not exist "%RAW_SOURCE%\" (
    echo [FATAL] 源目录不存在!
    echo   路径: %RAW_SOURCE%
    echo   请检查:
    echo     1. NatSelect/Network/Proto/Source 是否存在
    echo     2. 路径大小写是否正确（Windows 通常不敏感，但需确认）
    goto :ERROR_EXIT
)
echo [OK] 源目录存在: %RAW_SOURCE%

:: ========== 3. 严格验证 Protoc ==========
if not exist "%RAW_PROTOC%" (
    echo [FATAL] Protoc 不存在!
    echo   路径: %RAW_PROTOC%
    echo   请确认 protoc-34.0-win64 文件夹与脚本同级
    goto :ERROR_EXIT
)
"%RAW_PROTOC%" --version >nul 2>&1
if errorlevel 1 (
    echo [FATAL] Protoc 无法执行! 请检查文件是否损坏
    goto :ERROR_EXIT
)
for /f "tokens=*" %%i in ('"%RAW_PROTOC%" --version 2^>nul') do set PROTOC_VER=%%i
echo [OK] Protoc 版本: !PROTOC_VER!

:: ========== 4. 创建目标目录 ==========
if not exist "%RAW_TARGET%\" mkdir "%RAW_TARGET%" 2>nul
if not exist "%RAW_TARGET%\" (
    echo [FATAL] 无法创建目标目录: %RAW_TARGET%
    goto :ERROR_EXIT
)
echo [OK] 目标目录: %RAW_TARGET%

:: ========== 5. 检查源目录是否有 proto 文件 ==========
dir /b "%RAW_SOURCE%\*.proto" >nul 2>&1
if errorlevel 1 (
    echo [WARNING] 源目录中未找到 .proto 文件!
    echo   路径: %RAW_SOURCE%
    echo   请确认文件是否放在正确位置
    goto :ERROR_EXIT
)
for /f %%i in ('dir /b "%RAW_SOURCE%\*.proto" ^| find /c /v ""') do set COUNT=%%i
echo [OK] 找到 %COUNT% 个 proto 文件

:: ========== 6. 执行编译（关键：直接使用 RAW_SOURCE，避免 pushd 规范化问题）==========
echo.
echo [步骤2] 开始编译...
echo   命令: "%RAW_PROTOC%" --proto_path="%RAW_SOURCE%" --csharp_out="%RAW_TARGET%" --csharp_opt=file_extension=.pb.cs "%RAW_SOURCE%\*.proto"
echo.

"%RAW_PROTOC%" ^
  --proto_path="%RAW_SOURCE%" ^
  --csharp_out="%RAW_TARGET%" ^
  --csharp_opt=file_extension=.pb.cs ^
  "%RAW_SOURCE%\*.proto"

if errorlevel 1 (
    echo.
    echo [FATAL] Protoc 编译失败! 请检查:
    echo   - proto 文件语法
    echo   - import 路径是否相对于 Source 目录
    echo   - 字段名拼写（如 sequeunce_id 应为 sequence_id）
    goto :ERROR_EXIT
)

:: ========== 7. 验证生成结果 ==========
dir /b "%RAW_TARGET%\*.pb.cs" >nul 2>&1
if errorlevel 1 (
    echo [WARNING] 编译命令返回成功，但未找到生成的 .pb.cs 文件!
    echo   请检查 Target 目录: %RAW_TARGET%
    goto :ERROR_EXIT
)
echo.
echo ========================================
echo   SUCCESS! C# 代码生成成功
echo   位置: %RAW_TARGET%
echo ========================================
explorer "%RAW_TARGET%"
goto :FINAL_EXIT

:ERROR_EXIT
echo.
echo ========================================
echo   SCRIPT FAILED - 请根据上方错误信息排查
echo ========================================
pause
exit /b 1

:FINAL_EXIT
echo.
echo [提示] 按任意键关闭窗口...
pause >nul
exit /b 0