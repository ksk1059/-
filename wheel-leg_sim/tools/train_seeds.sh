#!/usr/bin/env bash
# Runs the seed sweep a paper needs: the same configuration trained from several seeds, so a
# result can be separated from the luck of one initialisation.
#
#   tmux new -s train
#   tools/train_seeds.sh /data/runs 137 138 139 140 141
#   # Ctrl+B, D to detach
#
# Each seed is one process from start to finish. Training is a single long run: stopping and
# restarting loses the optimiser state, the observation normalisation statistics and the
# learning-rate schedule. Only the seed loop restarts the process.
#
# If a run dies, re-run this with just that seed; --resume picks up the checkpoint. The
# checkpoint interval is deliberately short because the server can reboot without notice
# (SERVER_OPS_1.md section 4-3).
set -u

OUT_ROOT="${1:?usage: train_seeds.sh <out-root> <seed> [seed...]}"
shift
SEEDS=("$@")
[ ${#SEEDS[@]} -gt 0 ] || { echo "give at least one seed"; exit 2; }

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_BIN="${ENV_BIN:-$HERE/Builds/linux-server/sim.x86_64}"
PYTHON="${PYTHON:-python}"
PROFILE="${PROFILE:-server}"

[ -x "$ENV_BIN" ] || { echo "player not found or not executable: $ENV_BIN"; exit 3; }
cd "$HERE"

echo "project   $HERE"
echo "player    $ENV_BIN"
echo "profile   $PROFILE"
echo "seeds     ${SEEDS[*]}"
echo "out root  $OUT_ROOT"

failed=()
for S in "${SEEDS[@]}"; do
  RUN_ID="go2w_s${S}"
  OUT="$OUT_ROOT/$RUN_ID"
  echo
  echo "=== $RUN_ID  ($(date -u +%Y-%m-%dT%H:%M:%SZ)) ==="

  # tools/train.py, not mlagents-learn: the trainer works either way, but only this one can
  # export the .onnx at the end. See the comment block in tools/train.py.
  RESUME=""
  [ -d "results/$RUN_ID" ] && RESUME="--resume"

  "$PYTHON" tools/train.py config/ppo.yaml \
    --run-id="$RUN_ID" \
    $RESUME \
    --env="$ENV_BIN" \
    --no-graphics \
    --env-args --profile "$PROFILE" --seed "$S" --out "$OUT" \
    || { echo "!! $RUN_ID failed with $?"; failed+=("$S"); continue; }

  if [ -f "results/$RUN_ID/WheelLegGo2W.onnx" ]; then
    echo "   exported results/$RUN_ID/WheelLegGo2W.onnx"
  else
    # The run trained but produced no policy, which is worse than a crash because the logs
    # look complete. Name it rather than let the sweep look successful.
    echo "!! $RUN_ID produced NO .onnx - the export step failed"
    failed+=("$S")
  fi
done

echo
if [ ${#failed[@]} -eq 0 ]; then
  echo "all ${#SEEDS[@]} seed(s) finished and exported"
else
  echo "FAILED seeds: ${failed[*]}"
  exit 1
fi
