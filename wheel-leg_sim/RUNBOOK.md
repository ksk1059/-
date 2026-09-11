# 실행 절차 — Go2-W 휠-레그 강화학습 시뮬

프로그램은 한 벌이다. 로컬과 서버는 **같은 빌드, 같은 씬, 같은 코드**이며 프로파일 파일만 다르다.

---

## 0. 최초 1회 세팅 (에디터)

1. Unity 6000.4.0f1 로 `wheel-leg_sim` 열기
2. `WheelLeg / Import Go2-W URDF` — `Assets/Robots/go2w/go2w_description.urdf` 를 임포트
3. `WheelLeg / Validate Imported Go2-W` — 12 관절 + 4 휠 전부 찾는지, 휠이 자유회전인지 확인
   - 휠이 제한 조인트로 들어오면 `WheelLeg / Fix Wheel Joints` 실행
4. `WheelLeg / Build Robot Prefab` — `Assets/Resources/Go2W.prefab` 생성
5. `WheelLeg / Build Simulation Scene` — `Assets/Scenes/WheelLegSim.unity` 생성 및 빌드 씬으로 등록

씬은 하나뿐이다. 학습영역 수는 런타임에 `run.areas` 로 만들어지므로 1개→32개 확장에 씬 수정이 없다.

---

## 1. 로컬 확인 (에디터 Play)

```
config/profiles/local.yaml  → areas 1, 평지, 로깅 켜짐, 결정성 켜짐,
                              controller_type: baseline_gait_pd
```

Play 를 누르면 `SimBootstrap` 이 커맨드라인 없이 기본 프로파일(`local`)로 부팅한다.
콘솔에 다음 한 줄이 나와야 한다.

```
[WheelLeg] mode=Train controller=baseline_gait_pd seed=1000 areas=1 out=... log=Parquet
```

로컬 프로파일이 `policy` 가 아닌 이유: 에디터 Play 는 커맨드라인 인자를 못 받고 트레이너도
안 붙는다. 그 조합에서 ML-Agents 는 `Heuristic` 으로 폴백해 **매 스텝 전부 0인 행동**을 내놓고,
에피소드는 정상 진행하며 로그도 다 쓴다. 즉 한 번도 지령받지 않은 로봇의 기록이 남는데
로그만 봐서는 "정책이 학습을 못 했다" 와 구분이 안 된다. 실제로 이 프로젝트에서 569 프레임을
그렇게 낭비했다. `SimBootstrap.VerifyActionSourceExists` 가 이제 그 조합을 exit 3 으로 거부한다.

### 사람 눈으로 확인할 항목 (SPEC 10-A 4·5·6)

에이전트가 스스로 통과 판정하지 않는다.

| # | 확인 | 방법 |
|---|---|---|
| 4 | Heuristic 자세 유지 | `--controller baseline_gait_pd` 로 Play, 로봇이 서 있는지 |
| 5 | 휠 구동 | `--set command.vx_range=[1.0,1.0]` 로 Play, 앞으로 굴러가는지 |
| 6 | 리셋 | 넘어뜨린 뒤 스폰 지점·초기 자세로 복귀, 누적 오차 없는지 |

---

## 2. 설정 바꾸기

**코드는 건드리지 않는다.** 세 가지 방법뿐이다.

```bash
# 1) 프로파일 교체
./sim.x86_64 --profile server

# 2) 편의 플래그
./sim.x86_64 --profile server --seed 137 --areas 32 --out /data/runs/exp_137

# 3) 임의 경로 오버라이드 (존재하지 않는 키는 거부됨)
./sim.x86_64 --set terrain.difficulty=[0.2,0.8] --set reward.wheel_slip=-0.3
```

`--set` 은 이미 존재하는 키에만 적용된다. 오타는 조용히 기본값으로 떨어지지 않고 즉시 실패한다.

---

## 3. 학습 (트레이너가 프로세스를 띄운다)

**`mlagents-learn` 이 아니라 `tools/train.py` 로 띄운다.** 인자는 전부 그대로 전달된다.
직접 `mlagents-learn` 을 쓰면 학습은 되지만 **끝에서 ONNX export 가 실패해 정책을 못 꺼낸다**
(이유와 근거는 `tools/train.py` 파일 상단 주석에 있다).

```bash
tmux new -s train                       # SSH 끊겨도 살아남게
python tools/train.py config/ppo.yaml \
  --run-id=go2w_s137 \
  --env=./Builds/linux-server/sim.x86_64 \
  --no-graphics \
  --env-args --profile server --seed 137 --areas 32 --out /data/runs/go2w_s137
# Ctrl+B, D 로 detach
```

- 학습은 **하나의 긴 실행**이다. 중간에 끄고 다시 켜면 옵티마이저·정규화 통계·LR 스케줄이 날아간다.
- 끊겼으면 `--resume` 으로 이어간다. `checkpoint_interval` 은 짧게 잡혀 있다.
- 시드 3~5개로 반복한다. 이 반복만 프로세스 재기동이다.

```bash
tools/train_seeds.sh /data/runs 137 138 139 140 141
```

이 스크립트는 죽은 실행이 있으면 `--resume` 을 자동으로 붙이고, **학습은 끝났는데 `.onnx` 가
안 나온 시드를 실패로 표시한다.** 그 경우가 크래시보다 나쁘다 — 로그는 완주한 것처럼 보이는데
정책이 없다.

**Windows 로컬에서는 빌드가 ASCII 경로에 있어야 한다** (10절 2번 선행조건 B). 리눅스는 무관하다.

끝나면 `results/<run-id>/WheelLegGo2W.onnx` 가 나온다. 평가에 쓰려면 4절의 `.sentis` 변환을 거친다.

---

## 4. 평가 (트레이너 없음, 스스로 종료)

정책은 **`.sentis` 파일 경로**로 넘긴다. `.onnx` 가 아니다. ONNX 파서는 에디터 전용이라
플레이어가 못 읽는다. 정책마다 빌드를 새로 만드는 대신 에디터에서 한 번 변환한다.

```
1. 트레이너의 results/<run-id>/WheelLegGo2W.onnx 를 Assets/ 아래 아무 곳에 복사
   (Unity 가 ModelAsset 으로 임포트한다)
2. Project 창에서 선택 → WheelLeg/Convert Model To Sentis
3. 같은 폴더에 .sentis 가 생기고, 콘솔에 입출력 텐서 이름이 찍힌다
4. 그 파일을 --model 로 넘긴다
```

```bash
./Builds/linux-server/sim.x86_64 -batchmode -nographics \
  --profile server --mode eval --determinism true \
  --controller policy --model /data/models/go2w_s137.sentis \
  --out /data/runs/eval_s137
echo "exit=$?"
```

`PolicyController` 가 다른 두 베이스라인과 **같은 `IController` 경로**로 돈다. 세 조건 모두
동일한 60개 관측을 보고 동일한 16개 행동을 쓰므로 비교가 배선 차이로 오염될 수 없다
(SPEC 8절). ML-Agents 자체 추론 경로를 쓰지 않는 이유는 `BehaviorParameters.Model` 이
에디터 애셋을 요구해서 정책 1개당 빌드 1개가 되기 때문이다.

출력 텐서는 `deterministic_continuous_actions` 를 우선한다. 분포에서 샘플링하는
`continuous_actions` 를 쓰면 같은 시드 두 실행이 어긋나 비교가 무의미해진다. 모델에 예상한
이름이 없으면 실제 텐서 이름을 나열하고 실패한다 — 조용히 엉뚱한 텐서에 붙지 않는다.

**검증 완료 (2026-07-30).** 100000 스텝 학습 산출물로 실행한 결과:

```
[WheelLeg] policy loaded from ...WheelLegGo2W.sentis
           (input 'obs_0', output 'deterministic_continuous_actions')
[WheelLeg] evaluation complete: 10/10 episodes        exit=0

행동 비영값        160,000 / 160,000
action_0 고유값     9,688              (상수가 아니라 관측에 반응)
같은 시드 2회       parquet SHA256 일치
```

변환기가 보고한 텐서 이름:
`inputs: obs_0` / `outputs: version_number, memory_size, continuous_actions,
continuous_action_output_shape, deterministic_continuous_actions`

**한글 경로는 여기서는 문제가 없다.** 로그에는 `?숈뒿` 로 깨져 보이지만 파일은 정상 로드된다.
.NET 파일 API 는 유니코드를 처리한다. 2번 항목의 gRPC 네이티브 로더만 ANSI 문제가 있다.

baseline 만 돌릴 때는 `--model` 없이:

```bash
./Builds/linux-server/sim.x86_64 -batchmode -nographics \
  --profile server --mode eval --determinism true \
  --controller baseline_gait_pd \
  --out /data/runs/eval_gait_pd
echo "exit=$?"
```

- 한 프로세스가 `seeds.eval_list` 를 **순회**한다. 시드마다 재기동하지 않는다.
- 다 돌면 `Application.Quit(0)`. 실패는 0이 아닌 코드로 나온다.
  - `2` 설정 거부, `3` 셋업 실패(조인트 미해결 등), `4` 런타임 실패
- 평가는 `--determinism true` 로 돌린다. 비교 실험의 전제조건이다.

baseline 비교는 **같은 명령에 컨트롤러만 바꾼다.** 별도 씬도 별도 빌드도 없다.

```bash
./sim.x86_64 -batchmode -nographics --profile server --mode eval --determinism true \
  --controller policy --model /data/models/go2w_s137.sentis --out /data/runs/eval_policy
for C in baseline_gait_pd baseline_balance_lqr; do
  ./sim.x86_64 -batchmode -nographics --profile server --mode eval --determinism true \
    --controller $C --out /data/runs/eval_$C
done
```

---

## 5. 출력물

`--out` 디렉토리 아래에만 생긴다.

```
<out>/
├─ run_manifest.json      # argv 전체, 커밋 해시, 유니티 버전, 해결된 설정값 전부
└─ frames/
   ├─ area000_part00000.parquet
   └─ ...
```

- `terrain_seed`, `seed_band`, `controller_type` 이 매 프레임에 있다. 없으면 사후 분석이 불가능하다.
- 파일은 `logging.max_file_bytes` 마다 회전하고, 영역별 총량이 `logging.max_total_bytes` 를 넘으면 **경고 한 번 남기고 기록을 멈춘다.** 서버 디스크는 3명이 공유한다.

착수 전 예상 용량을 계산한다. 프레임당 182 컬럼 × 8 바이트 ≈ 1.5 KB, 50 Hz →
**영역당 약 262 MB/시간**. 32영역 12시간이면 약 100 GB 다. 여유 용량을 먼저 확인한다.

---

## 6. 서버 이행 전 확인 (SERVER_OPS_1.md 7절)

```bash
# 전역 상태
grep -rn "UnityEngine.Random" Assets/WheelLeg/        # 0건이어야 함
grep -rn "GameObject.Find"    Assets/WheelLeg/        # 0건이어야 함
grep -rn "FindObjectsByType"  Assets/WheelLeg/Runtime # 1건. SimBootstrap.VerifyNoStrayRobots 뿐 (9-3절)
grep -rnE '"[A-Z]:\\\\|/home/|/data/' Assets/WheelLeg/ # 절대경로 0건이어야 함
```

- `-batchmode -nographics` 기동 확인
- 최소 1시간 실행 후 메모리 증가 없는지 확인
- 결과물 로컬 회수 절차 확보

---

## 7. 매 실행마다 확인할 것 — `physics_probe.json`

`--out` 아래에 실행마다 생긴다. **첫 줄만 보면 된다.**

```json
{
  "joint_torque_columns_usable": true,
  "drive_gains_verified": true,
  "self_collision_pairs_ignored": 435,
  "imported_leg_force_limit": [ 23.7, 23.7, 35.55 ],
  "base_last_external_contact": null,
  "leg_drive_readback":   { "stiffness": 60, "damping": 3, "force_limit": 23.7 },
  "wheel_drive_readback": { "stiffness": 0,  "damping": 2, "force_limit": 23.7 }
}
```

| 필드 | 이게 아니면 |
|---|---|
| `drive_gains_verified: true` | 로봇이 무동력으로 돈 것이다. 실행 결과 전부 폐기 |
| `self_collision_pairs_ignored` > 0 | 0 이면 자기충돌이 안 꺼졌다. 아래 8-1절 |
| `imported_leg_force_limit` | `config` 의 `drive.leg_force_limit` 과 일치해야 한다 |
| `base_last_external_contact` | 지형 콜라이더가 아닌 **로봇 자기 링크 이름**이 보이면 자기충돌이 접촉 플래그를 오염시키는 중이다 |

`drive_gains_verified: false` 면 **로봇이 무동력 상태로 돈 것이다.** 그 실행의 결과는
전부 버린다. 이게 왜 있냐면 이 프로젝트에서 실제로 두 번 발생했기 때문이다.

1. `Awake` 에서 `Instantiate` 직후 `xDrive` 에 쓰면 ArticulationBody 계층이 아직
   구성 전이라 **쓰기가 조용히 버려진다.** → `FixedUpdate` 에서 리드백될 때까지 재적용.
2. URDF Importer 가 로봇 루트에 붙이는 `Control.Controller` 는 수동 키보드 데모
   컨트롤러인데, `Update` 마다 자기 인스펙터 값(stiffness 0, damping 0)으로
   **모든 관절의 xDrive 를 덮어쓴다.** → 프리팹 생성 시 제거 + 런타임에서 재확인.

둘 다 에러를 내지 않는다. 로봇은 멀쩡히 시뮬레이션되고, 그냥 래그돌로 넘어질 뿐이다.
학습 곡선만 보면 "정책이 학습을 못 했다" 와 구분이 안 된다.

---

## 8. 임포트가 조용히 만드는 함정 5개

전부 에러를 내지 않는다. 첫 실행에서 로봇이 부들거리다가 지면 위로 튀어오르고, 축 쳐져 눕고,
스폰 지점의 무언가에 발이 걸리고, 종아리가 없어 보인 증상의 원인이었다. 로그·컴파일·테스트
어디에도 나타나지 않았고 전부 실측으로만 잡혔다.

### 8-1. 형제 링크 자기충돌 — `physics.self_collision: false`

`ArticulationBody` 는 **직속 부모와의 충돌만** 제외한다. URDF Importer 는 고정 조인트까지
전부 별도 바디로 만들기 때문에, Go2-W 는 `*_calf` 아래에 서로 필터링되지 않는 형제 3개가
달린다 — `*_calflower`(정강이), `*_foot_motor`(휠 모터 하우징), `*_foot`(휠).

URDF 기준 정지 자세에서 측정한 값:

```
*_foot(r=86mm) <-> *_foot_motor    중심거리 38.1mm, 반지름합 145.7mm  -> 107.6mm 관통
*_calflower    <-> *_foot                    94.1mm,        124.1mm  ->  30.0mm 관통
*_calflower    <-> *_foot_motor              81.5mm,         90.2mm  ->   8.7mm 관통
```

휠 콜라이더가 자기 모터를 **상시 108 mm 삼킨 상태**다. 다리당 3쌍 × 4다리 = 12쌍을
솔버가 매 스텝 밀어내려 한다. 결과: 떨림 + 발사. 관측된 휠 접촉력 1066 N (기체 무게 192 N).

`self_collision: false` 면 `RobotDriver.DisableSelfCollision` 이 로봇 내부 콜라이더 **모든 쌍**에
`Physics.IgnoreCollision` 을 건다. 레이어 충돌 매트릭스를 쓰지 않는 이유는 그게 프로세스 전역
상태이고 32개 로봇이 한 프로세스를 공유하기 때문이다 (SERVER_OPS 2절).

보행 학습은 통상 자기충돌 없이 돌린다. 켜려면 URDF 에 인접 링크 제외 규칙을 먼저 넣어야 한다.

### 8-2. 중립 행동이 기립 자세가 아니었던 문제

Go2-W 는 앞다리와 뒷다리의 허벅지 리밋이 다르다.

```
FL/FR_thigh_joint  (-1.5708, +3.4907) rad   중점 +0.960 rad ( 55 deg)
RL/RR_thigh_joint  (-0.5236, +4.5379) rad   중점 +2.007 rad (115 deg)   <- 비대칭
```

행동값을 리밋 구간에 선형 대응시키면 행동 0 이 이 중점이 된다. 기립 자세 허벅지는 50 도이므로
**뒷다리에 65 도 차이**가 생기고, `60 Nm/rad × 1.134 rad = 68 Nm` 를 요구해 35.55 Nm 리밋에
포화한다. 학습 초기 정책은 0 근처를 내놓으므로 매 에피소드 첫 스텝마다 뒷다리를 최대 토크로
걷어차게 된다. 관측된 관절 속도 13 rad/s.

지금은 `LegJointLimits.ActionToTargetRad` 가 기립 자세 기준 잔차다.

```
target = stand_pose + action * robot.action_scale_deg,  관절 리밋으로 클램프
action = 0  ->  기립 자세
```

관측 채널(`NormalizeAngle`)은 여전히 리밋 전 구간 정규화다. 두 방향은 **의도적으로 다른 매핑**이고
이름도 다르게 뒀다. 행동 매핑을 리밋 중점으로 되돌리지 마라.

### 8-3. 임포트한 로봇을 시뮬레이션 씬에 남기면 안 된다

`WheelLeg/Import Go2-W URDF` 는 **열려 있는 씬에** 로봇을 떨어뜨린다. 그 씬이
`WheelLegSim` 이면 로봇이 씬에 저장된다. 런타임에는 `SimBootstrap` 이 prefab 으로 **또 하나**를
만들기 때문에 로봇이 2대가 된다.

남은 쪽은 `Configure` 를 안 받으므로 드라이브 게인이 0 이고 **월드 원점에서 래그돌로 눕는다.**
그 원점이 정확히 area 0 의 스폰 지점이다. 결과:

- 진짜 로봇이 시체에 발이 걸린다 (관측된 휠 접촉력 1910 N, 기체 무게 192 N)
- **다른 articulation** 이므로 접촉이 정상 지면 접촉으로 집계되어 `base_contact` 로 에피소드 종료
- 남은 쪽에 `WheelLegAgent` 가 붙어 있으면 **같은 behavior name 으로 두 번째 에이전트가 등록되어**
  매 스텝 60개 0 관측을 트레이너에 보낸다

`SimBootstrap.VerifyNoStrayRobots` 가 이제 부팅 시 exit 3 으로 거부한다. 런타임에서 씬 전체를
검색하는 곳은 여기 하나뿐이고, 6절 grep 이 잡는 전역 조회 금지의 취지(영역별 로직이 자기
서브트리 밖을 참조하는 것)와 반대 목적이므로 허용한 예외다.

프리팹을 다시 만들 때는 `Build Robot Prefab` 실행 후 **씬에서 로봇을 지우고 저장한다.**

### 8-4. 종아리가 안 보이는 것 (물리는 정상이었다)

`Go2W.prefab` 의 종아리 MeshFilter 10개가 `sharedMesh == null` 로 저장돼 있었다. 네 다리
종아리가 전부 안 그려져서 **허벅지로 땅을 짚는 것처럼 보인다.**

원인: 종아리 시각 메시는 `calf.stl` / `calf_mirror.stl` 이고, 65535 정점 한계에서
`calf_0/1/2`, `calf_mirror_0/1` 로 쪼개진다. `Build Robot Prefab` 이 그 애셋 임포트가 끝나기
전에 돌면 MeshFilter 가 비어 있고, 그 빈 상태가 프리팹에 구워진다. 애셋 자체와
`calf.prefab` 내부 GUID 참조는 정상이었다.

**물리에는 영향이 없었다.** 종아리 콜라이더는 URDF 실린더 프리미티브라 별개다
(`colliderSize=(0.026, 0.123, 0.049)` 정상). 그림만 틀렸다.

`WheelLeg/Repair Mesh Bounds` 로 이름 기준 재연결한다. `Build Robot Prefab` 도 저장 전에
같은 처리를 한다.

진단 도구: `WheelLeg/Audit Robot Prefab Geometry` — 링크별 렌더러·콜라이더 개수와 크기를
찍는다. `visualSize=(0.000, 0.000, 0.000)` 또는 `DRAWS NOTHING` 을 보면 된다.
`imu`, `radar`, `*_foot_motor`, `*_calflower`, `*_calflower1` 은 URDF 에 시각 메시가
없는 링크라 정상이다.

### 8-5. 머티리얼이 마젠타로 보이는 것

`.dae` 메시는 머티리얼을 모델 애셋에 내장(`materialLocation: 1`)한 채 들어오고, 임포터가
빌트인 파이프라인 셰이더(`Standard`)를 붙인다. 이 프로젝트는 URP 라서 컴파일이 안 되고
마젠타로 그려진다. 36개 렌더러 슬롯 중 12개가 해당됐다 (허벅지 4, 고관절 4, 몸통 데칼 4).

`WheelLeg/Fix Prefab Materials` 로 고친다. 순수 시각 문제이고 물리에는 영향이 없다.
다만 마젠타 로봇은 SPEC 10-A 4·5·6 을 눈으로 판정하는 걸 방해하므로 고쳐둔다.

---

## 9. 물리 건강 기준선 (8절 수정 후 실측)

`baseline_gait_pd`, 평지, 14 에피소드. 이 값에서 크게 벗어나면 회귀다.

| 지표 | 값 | 판정 근거 |
|---|---|---|
| `base_contact` 종료 | **0 건** | 몸통은 평지에서 지면에 닿을 수 없다 |
| 휠 접촉력 최대 | 211~295 N (기체 무게의 1.10~1.54배) | 4족 착지에서 정상 범위 |
| step 0 최대 속도 | 0.1974 m/s | `g × 0.02 s = 0.196`. 스폰 갭 자유낙하 1스텝 그 자체 |
| step 1 관절속도 최대 | 2.48 rad/s | 슬램 없음 |
| 정강이 관절 포화 | 0.0000 | |
| 고관절·허벅지 포화 | 1~12% | 23.7 Nm 는 URDF 실제 액추에이터 한계다 |

`stiffness` 단위는 **Nm/rad** 다 (Nm/deg 아님). 실측: 오차 -0.0609 rad → 토크 -3.711 Nm
(예측 -3.65). 드라이브 법칙 자체에는 문제가 없다.

### 쪼그린 자세를 봤을 때 — 보상 문제인가 강성 문제인가

둘은 똑같이 보이는데 원인도 대책도 다르다. **어느 컨트롤러였는지가 먼저다.**

**`baseline_gait_pd` / `baseline_balance_lqr` 이면 보상과 무관하다.** 둘 다 고정 제어기이고
보상을 입력으로 받지 않는다. 학습으로 전략을 채택할 구조가 아니다. 이때 쪼그림은 위치
드라이브가 하중에 밀리는 것이다. `오차 = 토크 / leg_stiffness` 이므로:

```
실측 (baseline_gait_pd, 평지)
  허벅지 지령 대비 오차 평균  5.65°      정강이 2.43°
  kp=60 에서 예측되는 처짐    7.96°      6.09°      (= 평균토크 8.33 / 6.38 Nm 를 60 으로 나눈 값)
  기하학적 기립 높이 0.3684 m  vs  실제 평균 0.2983 m   차이 0.0701 m
  토크 한계 접촉 프레임        0.24 ~ 0.30%            <- 포화가 아니다
```

토크가 포화하지 않으므로 힘이 모자란 게 아니라 **강성이 유한한 것**이다.
`leg_stiffness` 를 올리면 줄어든다.

**단, 실기 액추에이터도 강성이 유한하다.** sim-to-real 을 주장한다면 처짐 자체는 오히려
현실적이다. 처짐을 없애려고 `leg_stiffness` 를 키우는 것은 실기와 멀어지는 방향일 수 있다.

**`policy` 였다면 보상 문제일 가능성이 높다.** 무게중심이 낮으면 안 넘어지고, 안 넘어지면
`posture_up` + `alive` 를 계속 받는다. 그게 총 보상의 65% 다 (6-1절). 이 구조에서
"쪼그려서 버티기" 는 전형적인 보상 해킹이고, 100000 스텝 정책이 이미 그 방향이었다
(명령 방향은 상관계수 0.84 로 배우고 크기를 0.0048 로 눌렀다).

정리하면:

| 본 것 | 컨트롤러 | 원인 | 손댈 곳 |
|---|---|---|---|
| 쪼그림 + 제자리 발구름 | baseline | 유한 강성 + 고정 보행 주기 | `leg_stiffness`, `gait_frequency_hz` |
| 쪼그림 + 명령 무시 | policy | 보상 구조 | 6-1 후보 1번 |

### 아직 튜닝 안 된 것

전부 근거 없는 초기값이다 (SPEC 11절). 튜닝했다고 보고하지 않는다.

- **`baseline_gait_pd` 는 로봇을 세우지 못한다.** 에피소드 중간값 54 스텝(약 1.1초) 후
  `tilted` 또는 `low_base` 로 넘어진다. `roll` 이 단조 증가한다. 물리 결함이 아니라
  개루프 트롯 + 약한 자세 PD 가 휠 달린 발을 못 잡는 것이다. **SPEC 10-A 4번(자세 유지)은
  현재 통과하지 못한다.**
- **선회 항이 전진 항을 압도한다.** `forward = vx × 11.6/20 = vx × 0.58` 인데
  `turn = wz × 1/max|wz| = wz × 1.0` 이라, `wz_range: [-1,1]` 에서 선회 명령만으로 휠이
  ±1 에 붙는다. 무작위 방향으로 돌아다니는 원인. `baseline.gait_pd.wheel_speed_per_command`
  를 올리거나 `command.wz_range` 를 좁히는 쪽으로 손볼 수 있다.
- **정착 높이 0.289 m** vs 기하학적 기립 높이 0.368 m. `leg_stiffness: 60 Nm/rad` 에서
  허벅지에 약 10 Nm 가 필요하므로 0.17 rad 처짐이 계산과 일치한다. 결함 아님.

---

## 10. 논문 실험 착수 전 남은 일 (지우지 말 것)

물리 기반은 검증됐고(9절 기준선) 검증 파이프라인이 비어 있다. 순서가 중요하다:
2번을 먼저 해야 5·6·7 을 판단할 근거가 생긴다. 베이스라인부터 손대는 것은 순서가 거꾸로다.

| # | 항목 | 상태 |
|---|---|---|
| 1 | 평가 경로 구현 (ONNX 정책을 독립 실행으로) | **완료. 실제 학습 모델로 검증됨.** 아래 1-1 |
| 2 | 로컬 1영역 짧은 학습 1회 — 왕복 성립, 처리량 측정 | **왕복 성립 확인.** 아래 2-1 |
| 3 | 같은 시드 2회 → 궤적 일치 확인 | **통과.** 아래 참조 |
| 4 | 베이스라인 2개가 로봇을 세우도록 조정 | 9절. 현재 1.1초 후 넘어짐, SPEC 10-A 4번 미통과 |
| 5 | 32영역 실행 — 메모리 증가, 처리량, 로그 용량, 32영역 재현성 | **측정 완료. 아래 5-0.** 서버 실전 확인은 연결 후 |

### 5-0. 32영역 실측 (2026-07-30, Windows 플레이어, `--profile server`)

**메모리 — 누수 없음**

```
 30 s   1163.6 MB
 50 s   1173.5 MB
150 s   1174.5 MB
311 s   1175.0 MB
```

50 s 이후 261초 동안 증가량 **1.5 MB**. 초기 10 MB 는 워밍업이다.
32영역 총 1175 MB = 영역당 약 37 MB.

**처리량**

```
1영역   84.6 결정스텝/s
32영역  810 결정스텝/s   (5분 소크, 252,050행 / 311 s)
```

32배 영역에 **9.6배**. 물리가 단일 스레드라 선형이 아니다.
영역당으로는 84.6 → 25.3 스텝/s (3.3배 느려짐).

서버 시간 산정 근거:

```
10M 스텝  = 3.4 시간
50M 스텝  = 17 시간
```

**로그 용량 — 실측 1489.1 바이트/행** (추정 1.5 KB 와 일치)

```
810 행/s x 1489 B = 1.21 MB/s = 4.34 GB/시간 (32영역 전체)
12시간 실행 = 약 52 GB
```

5절의 "32영역 12시간 = 약 100 GB" 는 실시간 기준 추정이었다. **실측은 약 52 GB** 다.
32영역에서 영역당 처리량이 실시간보다 느리기 때문이다. 그래도 공유 디스크에서는 큰 값이다.

**32영역 재현성 — 통과**

같은 시드로 eval 스윕 2회, 32개 파트 파일 전부 해시 일치:

```
evalA  parts=32 areas=32 rows=18,146 episodes=100 bytes=27,020,770  exit=0
evalB  parts=32 areas=32 rows=18,146 episodes=100 bytes=27,020,770  exit=0
       -> 모든 파트 해시 일치
```

즉 아래 5-1 의 위험은 **`enhanced_determinism: true` 인 빌드에서는 발생하지 않는다.**
에피소드 종료 순서가 결정적이라 공유 카운터의 시드 배분도 결정적으로 떨어진다.

**단, 이 빌드는 결정성이 켜진 채로 구워졌다.** 서버 학습 빌드는 `false` 이므로
이 결과가 그대로 적용되지 않는다. 평가는 항상 결정성 켠 빌드로 돌려야 한다.

거친 지형(`difficulty: [0,1]`)에서 `baseline_gait_pd` 의 종료 분포:
`tilted` 58 / `low_base` 41 / `timeout` 1, 에피소드 길이 중앙값 74 스텝.

### 5-1. 결정성이 꺼진 빌드에서는 32영역 평가 재현성이 깨질 수 있다

평가 시드 목록은 **공유 카운터 하나**가 배분한다 (`EvaluationSeedSource`). 어느 영역이 어느
지형을 받는지는 **에피소드가 끝나는 순서**로 결정된다.

결정성이 켜진 빌드에서는 종료 순서가 결정적이므로 배분도 결정적이다 — 5-0 에서 확인했다.
**결정성이 꺼진 빌드에서는 종료 순서가 흔들리고 지형 배분 자체가 실행마다 달라진다.**
시드를 고정해도 비교가 성립하지 않는다.

평가를 항상 결정성 켠 빌드로 돌리면 문제가 없다. 그렇게 못 하는 상황이 오면 선택지는 두 개다:

- 평가는 항상 1영역 × 여러 프로세스로 돌린다 (영역별 시드 배분이 사라진다)
- 또는 시드를 영역에 **미리 정적으로 배분**한다 (영역 i 가 인덱스 i, i+32, i+64... 를 받는다).
  공유 카운터가 없어지므로 종료 순서와 무관해진다
| 6 | 보상 가중치·PPO·지형 난이도·관측 노이즈 조정 | **측정 완료. 아래 6-1.** 현재 보상의 최적해는 "정지" 다 |

### 6-1. 현재 보상 구조의 최적해는 얼어붙는 것이다 (실측)

100000 스텝 학습의 프레임 로그 101001 행을 실제 몸체 속도로 구간을 나눠 스텝당 보상을 뽑았다.

```
속도 m/s        보상     lin_track   slip      posture
0.00-0.02      0.9805    0.4562   -0.0951    0.4974      <- 가장 높다
0.02-0.05      0.9397    0.4500   -0.1029    0.4975
0.05-0.10      0.9376    0.4590   -0.1060    0.4975
0.10-0.20      0.9317    0.4565   -0.1092    0.4976
0.20-0.35      0.9079    0.4489   -0.1234    0.4978
0.35-0.60      0.8752    0.4478   -0.1533    0.4980
0.60+          0.7628    0.4193   -0.2415    0.4978      <- 가장 낮다
```

**보상이 속도에 대해 단조 감소한다. 움직이면 스텝당 -0.1058.**

`lin_track` 이 모든 구간에서 평평한 것(0.449~0.459)이 핵심이다. 빨리 움직여도 추종 보상이
오르지 않는다. 명령과 실제 운동이 무관하기 때문이다:

```
corr(cmd_vel_x,  전진속도)  = +0.0225
corr(cmd_vel_wz, 요레이트)  = +0.0072
```

그래서 추종 항은 "정지 상태" 값에 머물고 슬립 페널티만 속도에 비례해 커진다.
PPO 가 정지로 수렴한 것은 보상이 시킨 대로 한 것이다. **학습 곡선이 평평한 것은
시뮬 결함이 아니다.**

**항목별 스텝당 기여 (총 0.9191)**

```
posture_up          0.4977   54.1%   <- 최대 0.5. 서 있기만 하면 무조건
lin_vel_tracking    0.4529   49.3%   <- 최대 1.0. 정지해도 받는 값
ang_vel_tracking    0.2035   22.1%   <- 최대 0.5. 정지해도 받는 값
joint_torque       -0.1701  -18.5%   <- 서 있는 것만으로 발생, 회피 불가
wheel_slip         -0.1178  -12.8%
alive               0.1000   10.9%   <- 무조건
leg_action_rate    -0.0264   -2.9%
나머지 4개 합       -0.0207

무조건 받는 것 (posture + alive) = 0.5977 = 총합의 65.0%
```

**`tracking_sigma: 0.25` 가 너무 넓다.** 정지한 로봇이 받는 추종 보상:

```
|cmd| 0.25 m/s -> lin 0.779 / 1.0     <- 저속 명령은 사실상 공짜
|cmd| 0.50 m/s -> lin 0.368 / 1.0
|cmd| 0.75 m/s -> lin 0.105 / 1.0
|cmd| 1.00 m/s -> lin 0.018 / 1.0
```

**조정 후보 (전부 근거 없는 초기값, SPEC 11절)**

1. `posture_up` 0.5 → 0.1~0.2, `alive` 0.1 → 0. 무조건 받는 65% 를 줄인다
2. `tracking_sigma` 0.25 → 0.05 근방. 저속 명령을 공짜로 만들지 않는다
3. `wheel_slip` -0.1 → 낮춘다. **움직임을 손해로 만드는 유일한 항목**이다
   (정지 -0.095 → 0.35~0.6 m/s 에서 -0.153). 슬립 계산식 자체는 정상이다 —
   `rim_speed - body_speed` 라 구르는 바퀴는 0이 된다. 확인했다
4. `joint_torque` -0.0002: 서 있기만 해도 -0.17 이 나간다. 중력 보상 토크를 빼거나 가중치를 낮춘다

**학습된 정책 자체가 같은 결론을 독립적으로 확인해준다.** 100000 스텝 산출물을 평가로 돌리면:

```
corr(cmd_vel_x, action_12)  = +0.8407     <- 명령 -> 휠 행동 방향은 배웠다
|action| 평균                = 0.0048      <- 크기를 0 근처로 눌렀다
```

0.0048 x 20 rad/s = 0.1 rad/s = 초속 0.0086 m. **방향은 맞게 배우고 크기를 버렸다.**
움직이면 손해이기 때문이다. 보상 설계가 정책 파라미터에 그대로 찍힌 셈이다.

**단정하지 않는 것:** 100000 스텝은 연막 시험이지 학습 실험이 아니다. 보행 학습은 보통
10~50M 스텝을 쓴다. 위 표는 "매 순간의 즉각적 유인이 정지를 가리킨다" 는 구조적 사실이고,
학습량을 늘리면 극복될 수도 있다. 다만 그 전에 1~3 을 고치는 것이 싸다.

**슬립이 큰 이유 하나는 7-1 이다.** 휠 마찰이 설정값이 아니라 (지형+0.6)/2 로 들어간다.
7-1 을 고치면 슬립이 줄어 3번의 필요량도 달라진다. 7 을 먼저 정하고 6 을 조정한다.
| 7 | 물리 충실도 | **7-1 고침 · 7-2 넣고 켬 · 7-3 구조만(값 0) · 7-4 팀 합의 대기** |

### 7-1. 휠 마찰이 설정값과 달랐다 — **고침 (2026-07-30)**

지형에는 설정에서 뽑은 `PhysicsMaterial` 이 붙는데 **로봇 콜라이더에는 재질이 없었다.**
PhysX 기본값(마찰 0.6)이 쓰이고, 두 재질의 결합 방식이 모두 Average 이므로:

```
이전:  effective = (terrain_friction + 0.6) / 2
       config terrain.friction: [0.6, 1.0]  ->  실제 접촉 [0.6, 0.8]
```

`friction: 1.0` 으로 설정해도 접촉은 0.8 을 받았다.

**수정.** `RobotDriver.SetFriction` 이 로봇의 모든 콜라이더에 재질을 붙이고, 에피소드마다
`TrainingArea.BeginEpisode` 가 그 회차 지형이 뽑은 값을 그대로 밀어넣는다.
양쪽이 같은 값이면 Average(f, f) = f 이므로 **설정값이 그대로 타이어-지면 계수가 된다.**

- 재질은 **로봇마다 하나**다. 32영역이 각자 시드에서 다른 마찰을 뽑으므로 공유하면
  전부 마지막에 쓴 값을 받는다 (SERVER_OPS 2절)
- `frictionCombine` 을 명시적으로 Average 로 지정한다. PhysX 는 두 재질 중 열거값이 큰
  규칙을 택하므로, 비워두면 결과가 지형 쪽 설정에 좌우된다
- `PhysicsMaterial` 은 `new` 로 만들어 씬 소유가 아니다. `OnDestroy` 에서 해제한다
- `physics_probe.json` 의 `robot_collider_friction` 으로 매 실행 확인한다. 0 이면 재질이
  안 붙은 것이고 모든 접촉이 PhysX 기본값 0.6 으로 돌고 있다는 뜻이다

**실측 확인.** 어느 실행에서 지형이 0.860422 를 뽑았고 프로브가 같은 값을 보고했다.

```
이전이었다면  (0.860422 + 0.6) / 2 = 0.730     실제 접촉 계수
지금                                  0.860     +17.8%
```

### 7-2. 모터의 토크-속도 특성 — **넣음, 켜짐 (2026-07-30)**

이전에는 `force_limit` 이 속도와 무관한 상수였다. 실제 모터는 고속에서 정지토크를 못 낸다.
그대로 두면 정책이 **실기가 낼 수 없는 토크에 의존하도록 학습**한다 — sim-to-real 이
빠지는 전형적인 함정이다.

```
tau_max(w) = force_limit * (1 - |w| / no_load_speed)      0 으로 하한
```

`RobotDriver.SetLegTargetsRad` / `SetWheelTargetVelocitiesRad` 가 제어 주기마다
`xDrive.forceLimit` 을 갱신한다. `xDrive` 를 쓰는 곳이 여기 한 군데뿐이라
`FixedUpdate` 의 게인 검증과 경합하지 않는다.

**값의 출처는 URDF 의 `<limit velocity=...>` 다.** 지어낸 값이 아니다.

```
             effort(Nm)   velocity(rad/s)
hip            23.7           30.1
thigh          23.7           30.1
calf           35.55          20.07
foot(wheel)    23.7           30.1
```

**단, "최대 관절 속도 = 무부하 속도" 는 가정이다.** 실측한 모터 곡선이 아니다. 지금 있는
데이터로 할 수 있는 가장 강한 주장이고, 설정값이므로 팀이 실제 데이터를 얻으면 교체하면 된다.

```yaml
torque_speed_derating: true
leg_no_load_speed: [30.1, 30.1, 20.07]
wheel_no_load_speed: 30.1
```

`torque_speed_derating: false` 로 끄면 상수 한계로 돌아간다. `no_load_speed` 가 0 이하면
설정 로드가 거부한다 — 0 으로 나누면 모든 토크 한계가 조용히 0 이 되어 "못 움직이는 로봇" 과
구분이 안 되기 때문이다.

**실측 (같은 시드·지형·컨트롤러, `--set robot.drive.torque_speed_derating=` 만 다름)**

```
                              derate_on   derate_off
reward_total / step             0.8642      1.0533
  reward_wheel_slip            -0.2053     -0.1433
  reward_joint_torque          -0.2712     -0.2269
평균 에피소드 길이               183.8       380.0
|관절 토크| 최대 (Nm)            34.26       35.55
|휠 토크|  최대 (Nm)             23.28       23.70
|휠 속도|  최대 (rad/s)          31.81       22.96
```

토크 한계가 더 이상 URDF effort 상한에 붙지 않는다 (35.55 -> 34.26). 로봇이 절반 빨리
넘어진다 — 토크 여유가 줄었으니 당연하다. **6번의 조정 목표가 이 변경으로 달라졌다.**
7 을 6 보다 먼저 한 이유가 이것이다.

**알려진 한계 — 제동 토크.** 이 1차 모델은 속도가 무부하 속도를 넘으면 토크 한계를 0 으로
만든다. 그래서 **과회전한 휠을 모터가 멈출 수 없다** (측정된 휠 속도 최대 31.81 rad/s >
무부하 속도 30.1). 실제 BLDC 는 반대다: 회전 방향과 반대로 거는 토크는 역기전력이 전압을
돕기 때문에 고속에서 오히려 **더 크다.**

물리적으로 맞는 개선은 감쇠를 **구동 방향에만** 적용하는 것이다 (토크와 속도의 부호가
같을 때만). 지금은 PhysX 의 `forceLimit` 이 부호 없는 대칭 클램프라 그대로는 안 되고,
드라이브가 내려 할 토크의 부호를 추정해야 한다 (속도 드라이브면
`sign(targetVelocity - currentVelocity)`).

**안 넣었다.** 물리 모델을 바꾸는 선택이라 팀 합의 항목(7-4)에 속한다. 지금 상태는
실기보다 **불리한 쪽으로** 틀렸다는 점만 분명히 해둔다 — 제동을 못 하는 로봇으로 학습하면
실기에서는 더 잘 되지 덜 되지는 않는다. 안전한 방향의 오차이지만 오차는 오차다.

### 7-3. 행동 지연 — **구조만 넣음, 기본값 0**

관측 지연은 있었다 (`observation.delay_steps`). 지령이 모터에 도달하는 지연은 없었다.
둘은 다른 값이다 — 하나는 감지, 하나는 구동이다.

`ActionDelayLine` 이 항상 컴파일되고 항상 실행된다. `robot.action_delay_steps: 0` 이면
그대로 통과시킨다. 이게 출하 기본값이다.

**0 인 이유:** URDF 에 지연 정보가 없다. 측정 없이 숫자를 넣으면 근거 없는 초기값이
하나 느는 것이고 논문에서 "왜 그 값인가" 를 답할 수 없다 (SPEC 11절).
**팀이 실기 제어 루프 지연을 재면 그 숫자만 넣으면 된다. 코드 변경이 아니라 설정 변경이다.**
1 스텝 = 0.02 초.

지연된 지령은 드라이브에만 간다. 관측의 이전-행동 채널과 행동변화율 보상은 **정책이 고른**
행동을 그대로 쓴다. 정책이 한 일로 평가받아야 하기 때문이다.

백래시는 PhysX 로 표현하기 어렵고 얻는 것이 적어 제외를 권한다.

### 7-4. 아직 팀 합의가 필요한 하드웨어 항목

- **부품 질량·관성**: 지금은 URDF 값 그대로다 (총 19.533 kg). 8-4 절에서 관성이 없던 링크
  8개에 무시 가능한 값을 주입한 것 외에는 손대지 않았다
- **신호 지연**: 7-3, 값 미정
- **모터 곡선**: 7-2, URDF 한계로 근사 중
- **환경 조건** (지면 재질, 온도에 따른 토크 저하 등): 모델 없음

**세 항목 모두 동역학을 바꾼다.** 이 변경 이전에 뽑은 학습 결과(`results/smoke_ascii` 등)는
비교 대상이 될 수 없다. `run_manifest.json` 에 `torque_speed_derating`,
`leg_no_load_speed`, `action_delay_steps` 가 기록되므로 사후 구분은 가능하다.

**설계 원칙: 값은 설정, 구조는 코드.** 하드웨어 세부(질량, 신호 지연, 모터 곡선, 환경)는
팀 합의가 필요한 항목이라, 합의되면 **설정 파일 숫자만 바꾸면 되도록** 구조를 먼저 넣었다.
기능을 나중에 붙이는 것이 아니라 항상 컴파일되어 있고, 중립값으로 꺼져 있을 뿐이다
(SPEC 0절 "나중에 미루지 않는다").

**2번 선행조건 A — 파이썬 환경.** 버전은 설치된 패키지의 `Documentation~/Installation.md` 에
명시된 값이다. 임의로 올리면 통신 프로토콜(1.5.0)이 안 맞는다.

```bash
conda create -n mlagents python=3.10.12 -y
conda run -n mlagents python -m pip install mlagents==1.1.0
# mlagents 1.1.0 이 pkg_resources 를 import 하는데 setuptools 81+ 에서 제거됐다
conda run -n mlagents python -m pip install setuptools==80.9.0
```

설치 확인된 조합: Python 3.10.12 / mlagents 1.1.0 / torch 2.13.0+cpu / numpy 1.23.5 /
protobuf 3.20.3 / Communicator API 1.5.0.

**2번 선행조건 B — 경로에 한글이 있으면 학습이 안 된다.**

이 프로젝트는 `C:\<user>\학습\` 아래에 있다. ML-Agents 의 gRPC 통신 모듈은
네이티브 DLL 을 ANSI 코드페이지로 로드해서, 비ASCII 경로에서는 실패한다.

```
Error loading native library
  "C:\<user>\?숈뒿\wheel-leg_sim\...\Plugins\x86_64\grpc_csharp_ext.x64.dll"
Couldn't connect to trainer on port 5005 using API version 1.5.0. Will perform inference instead.
```

**"Will perform inference instead" 가 위험한 부분이다.** 연결에 실패해도 플레이어는 계속 돌고,
정책이 없으니 매 스텝 0 행동을 낸다. 즉 아무것도 학습하지 않는 실행이 정상 종료된다.
`VerifyActionSourceExists` 가 이걸 잡아서 exit 3 으로 죽는다 — 이 검사가 없었으면
"학습이 안 되네" 로만 보이고 원인을 몇 시간 찾았을 것이다.

우회: **빌드만 ASCII 경로로 복사해서 그걸 `--env` 로 넘긴다.** 프로젝트는 안 옮겨도 된다.
`mlagents-learn` 자체는 파이썬이라 한글 경로에서 정상 동작한다.

```bash
cp -r Builds/win64 /c/wlsim_build
cd <project>
python -m mlagents.trainers.learn config/ppo.yaml --run-id=r1 --force \
  --env=C:\wlsim_build\sim.exe --no-graphics \
  --env-args --profile local --controller policy --areas 1 --out C:\wlsim_build\runs\r1
```

에디터에서 Play 로 학습하는 방법은 **쓸 수 없다.** 에디터가 로드하는 gRPC DLL 도 같은 한글
경로 아래에 있다. 에디터 학습이 필요하면 프로젝트를 ASCII 경로로 옮겨야 한다.

서버(리눅스)에서는 이 문제가 없다. 리눅스 경로는 UTF-8 이다.

### 2-1. 왕복 성립 확인 (2026-07-30)

```
[INFO] Connected to Unity environment with package version 4.1.0 and communication version 1.5.0
[INFO] Connected new brain: WheelLegGo2W?team=0
[INFO] WheelLegGo2W. Step: 10000. Time Elapsed: 155.033 s. Mean Reward: 915.836. Std of Reward: 199.668.
```

- 패키지 4.1.0 ↔ 통신 1.5.0 일치. behavior name 일치. 관측 60 / 행동 16 수용
- `config/ppo.yaml` 의 값이 전부 그대로 반영됐다. 조용히 기본값으로 대체된 항목 없음

**처리량 (1영역, Windows, CPU, `threaded: False`)**

```
0     -> 10000 스텝:  155.0 s          64.5 스텝/s   (기동 시간 포함)
10000 -> 20000 스텝:  129.0 s          77.5 스텝/s   <- 정상 상태
```

결정 1회 = 물리 4스텝 x 0.005 s = 0.02 s 시뮬시간이므로
**77.5 스텝/s = 실시간의 1.55배**, `max_steps` 100000 은 약 22분.

서버 시간 산정은 32영역 값으로 한다 (5번 항목). 1영역 값을 32배 해서 쓰면 안 된다 —
물리가 단일 스레드라 선형 확장이 아니다.

에디터로 먼저 확인하는 것이 빠르다 (리눅스 빌드 불필요):

```bash
# config/profiles/local.yaml 의 controller_type 을 policy 로 바꾸고
mlagents-learn config/ppo.yaml --run-id=smoke_01 --force
# "Start training by pressing the Play button" 이 나오면 에디터에서 Play
```

이때 `VerifyActionSourceExists` 는 통과한다. 트레이너가 붙어 있으면
`Academy.Instance.IsCommunicatorOn` 이 true 이기 때문이다.

### 3번 결과 — 재현성 실측 (2026-07-30, Windows 플레이어)

```bash
# 같은 시드 2회
./sim.exe -batchmode -nographics --profile local --mode eval \
  --controller baseline_gait_pd --seed 4242 --out runs/det_a
./sim.exe ... --seed 4242 --out runs/det_b
# 대조군: 다른 시드
./sim.exe ... --seed 4243 --out runs/det_c
```

| | 결과 |
|---|---|
| 양쪽 exit code | `0`. 두 실행 모두 `evaluation complete: 10/10 episodes` |
| `det_a` vs `det_b` parquet SHA256 | `53A99D00...4EB2CB9F` — **완전히 동일. 비트 단위 재현** |
| `physics_probe.json` | 동일 |
| `det_c` (시드 4243) | `3DF82FE7...3AFDBAA6` — 다름. 시드가 실제로 실행을 지배한다 |

대조군이 필요한 이유: 시드를 무시하는 코드도 "같은 시드 2회 → 같은 결과" 를 통과한다.

**조건.** `physics.enhanced_determinism: true` 로 구운 Windows 플레이어, 1영역, 평지.
서버 학습 빌드는 `enhanced_determinism: false` 이므로 이 결과가 그대로 적용되지 않는다 (11절).
32영역 재현성은 5번 항목이다.

이 실행으로 함께 검증된 것:

- `WheelLeg/Build/Windows Player` 빌드 경로, `config/` 플레이어 옆 복사
- `--profile` / `--mode` / `--controller` / `--seed` / `--out` 커맨드라인 주입
- eval 모드가 `seeds.eval_list` 10개를 순회하고 `Application.Quit(0)` 로 스스로 종료
- `-batchmode -nographics` 기동 (6절 체크리스트 항목)

`--out` 은 **작업 디렉토리 기준**이다. exe 옆이 아니다. 절대경로를 쓰거나 cd 후 실행한다.

---

## 11. 알려진 제약

**Enhanced Determinism 은 런타임에 못 켠다.** Unity 가 PhysX 씬 생성 플래그로만
노출하고 `UnityEngine.Physics` 에 런타임 API 가 없다. 따라서 이 값은
`WheelLeg/Build/...` 빌드 시 `physics.enhanced_determinism` 설정값을 읽어
`ProjectSettings/DynamicsManager.asset` 에 **구워 넣는다.**

결과적으로 학습용(끔)과 평가용(켬) 빌드가 갈린다면 빌드가 2개 필요하다.
코드는 동일하고 설정만 다르므로 SPEC 0절의 취지는 지켜지지만,
"프로파일 교체만으로"는 이 한 항목에 대해 성립하지 않는다. 대안:

- 학습·평가 모두 켠 상태로 하나의 빌드를 쓰고 처리량 손해를 받아들인다, 또는
- 빌드를 2벌 만들고 `run_manifest.json` 의 `enhanced_determinism` 값으로 사후 구분한다.

어느 쪽을 택할지는 첫 처리량 측정 후 결정한다.
