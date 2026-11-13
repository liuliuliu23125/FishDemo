#!/bin/bash
# 批处理跑 Unity 的包装：先杀掉残留 Unity 进程、清项目锁，再执行。
#
# 为什么需要：本工程反复踩到
#   "Fatal Error! It looks like another Unity instance is running with this project open."
# 原因是被 timeout 截断的 Unity 进程不退出、仍持有项目锁（Temp/UnityLockfile）。
# 每次手动 taskkill + rm 容易漏，统一在这里处理。
#
# 用法：bash Tools/run-unity.sh <executeMethod> [额外参数...]
set -u
PROJ="E:/FishDemo"
UNITY="/c/Program Files/Unity/Hub/Editor/2022.3.62f2/Editor/Unity.exe"
METHOD="$1"; shift || true

# 1) 杀掉所有 Unity 进程（批处理环境无其它 Unity 会话）
for pid in $(tasklist //FI "IMAGENAME eq Unity.exe" //NH 2>/dev/null | awk '{print $2}' | grep -E '^[0-9]+$'); do
  echo "[run-unity] 终止残留 Unity PID=$pid"
  taskkill //F //PID "$pid" >/dev/null 2>&1
done
sleep 2
rm -f "$PROJ/Temp/UnityLockfile"

# 2) 跑
echo "[run-unity] method=$METHOD $*"
"$UNITY" -batchmode -projectPath "$PROJ" -executeMethod "$METHOD" "$@"
