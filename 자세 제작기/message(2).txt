#include <Arduino.h>

/*
  ============================================================
  4족 스텝모터 통합 제어 코드


  일반 명령:
     1:360      = 모터1 +360도 회전
     1:-360     = 모터1 -360도 회전
     F:360      = 앞쪽 모터 2개 +360도 회전
     B:-360     = 뒤쪽 모터 2개 -360도 회전
     ALL:360    = 전체 모터 +360도 회전
     POS        = 현재 위치 출력
     SET        = 현재 위치를 초기 위치 0도로 저장
     RESET      = SET으로 잡은 초기 위치로 돌아가기
     RE         = RESET과 같음


  패킷 명령:
     @01LFA+0090    = LF를 SET 기준 +90도 위치로 이동
     @01LFRE00000   = LF 위치값을 0으로 리셋


  패킷 규칙:
     A  명령블록 = 다리2글자 + A  + 각도5글자
     RE 명령블록 = 다리2글자 + RE + 00000


  마찰 / 백래시 보정:
     이전 이동 방향과 반대 방향으로 움직일 때
     입력 이동량의 1 / BACKLASH_DIV 만큼 추가 이동한다.

  ============================================================
*/

const int MOTOR_COUNT = 4;
const int MAX_COMMAND_COUNT = 6;

// 모터 번호와 다리 이름 매칭
const int LF = 0;
const int RF = 1;
const int LB = 2;
const int RB = 3;

// 360도 입력 시 몇 스텝 움직일지 설정
const int STEPS_PER_360 = 500;

// 모터 속도 조절
const int STEP_DELAY = 5;

// 방향이 바뀔 때 추가로 더 움직이는 보정값
const int BACKLASH_DIV = 30;

// 모터 핀 설정
const int motor_pins[MOTOR_COUNT][4] = {
  {42, 44, 46, 48},  // 모터1 = LF
  {37, 39, 41, 43},  // 모터2 = RF
  {29, 31, 33, 35},  // 모터3 = LB
  {34, 36, 38, 40}   // 모터4 = RB
};

// 모터별 실제 회전 방향 보정
const int motor_dir[MOTOR_COUNT] = {
 -1,   // 모터1
  1,   // 모터2
  1,   // 모터3
  1    // 모터4
};

// L298N 4상 스텝 패턴
const int step_pattern[4][4] = {
  {HIGH, LOW,  HIGH, LOW},
  {LOW,  HIGH, HIGH, LOW},
  {LOW,  HIGH, LOW,  HIGH},
  {HIGH, LOW,  LOW,  HIGH}
};

// 현재 스텝 패턴 위치 저장
int step_pos[MOTOR_COUNT] = {0, 0, 0, 0};

// SET 기준으로 현재 위치 저장
long current_steps[MOTOR_COUNT] = {0, 0, 0, 0};

// 이전 이동 방향 저장
int last_dir[MOTOR_COUNT] = {0, 0, 0, 0};

// --------------------------------------------------------------------------------------------------
// 함수 선언
// --------------------------------------------------------------------------------------------------

void setup_motor_pins();
void run_command(String command);
void read_packet(String packet);
void run_move_list(long goal_steps[], bool goal_on[]);
void move_motors_by_steps(long logical_steps[]);
void step_one_motor(int motor_num, int direction);
void stop_all_motors();
void print_current_position();
void set_current_as_start();
void reset_to_start_position();

bool handle_command_block(int leg_num, String action_code, String value_text, long goal_steps[], bool goal_on[]);

long angle_to_steps(long angle);
long get_real_steps_with_backlash(int motor, long logical_steps);

int get_leg_index(String leg_text);
String get_leg_name(int leg_num);

bool check_signed_angle_5(String text);
long text_to_long(String text);

// --------------------------------------------------------------------------------------------------
// 시작 설정
// --------------------------------------------------------------------------------------------------

void setup() {
  Serial.begin(9600);
  Serial.setTimeout(30);
  Serial.println("START");
  setup_motor_pins();
}

// --------------------------------------------------------------------------------------------------
// 시리얼 입력 받기
// --------------------------------------------------------------------------------------------------

void loop() {
  if (Serial.available() > 0) {
    String line = Serial.readStringUntil('\n');
    line.trim();

    if (line.length() == 0) {
      return;
    }

    // @로 시작하면 패킷 명령으로 처리
    if (line.charAt(0) == '@') {
      read_packet(line);
    }
    // 아니면 일반 명령으로 처리
    else {
      run_command(line);
    }
  }
}

// --------------------------------------------------------------------------------------------------
// 모터 핀 초기화
// --------------------------------------------------------------------------------------------------

void setup_motor_pins() {
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    for (int pin = 0; pin < 4; pin++) {
      pinMode(motor_pins[motor][pin], OUTPUT);
      digitalWrite(motor_pins[motor][pin], LOW);
    }
  }
}

// --------------------------------------------------------------------------------------------------
// 일반 명령 처리
// --------------------------------------------------------------------------------------------------

void run_command(String command) {
  command.trim();
  command.toUpperCase();

  // 현재 위치 출력
  if (command == "POS") {
    print_current_position();
    return;
  }

  // 지금 위치를 기준 0으로 저장
  if (command == "SET") {
    set_current_as_start();
    Serial.println("SET complete");
    print_current_position();
    return;
  }

  // SET 위치로 복귀
  if (command == "RESET" || command == "RE") {
    Serial.println("RESET start");
    reset_to_start_position();
    Serial.println("RESET complete");
    print_current_position();
    return;
  }

  int colon_pos = command.indexOf(':');

  if (colon_pos < 0) {
    Serial.println("Unknown command");
    return;
  }

  String target_text = command.substring(0, colon_pos);
  String angle_text = command.substring(colon_pos + 1);

  target_text.trim();
  angle_text.trim();

  long angle = angle_text.toInt();
  long move_steps = angle_to_steps(angle);

  // 전체 모터 제어
  if (target_text == "ALL") {
    long steps_for_all[MOTOR_COUNT] = {0, 0, 0, 0};

    for (int motor = 0; motor < MOTOR_COUNT; motor++) {
      steps_for_all[motor] = move_steps;
    }

    move_motors_by_steps(steps_for_all);
    print_current_position();
    return;
  }

  // 앞쪽 모터 2개 제어
  if (target_text == "F") {
    long steps_for_front[MOTOR_COUNT] = {0, 0, 0, 0};

    steps_for_front[0] = move_steps;
    steps_for_front[1] = move_steps;

    move_motors_by_steps(steps_for_front);
    print_current_position();
    return;
  }

  // 뒤쪽 모터 2개 제어
  if (target_text == "B") {
    long steps_for_back[MOTOR_COUNT] = {0, 0, 0, 0};

    steps_for_back[2] = move_steps;
    steps_for_back[3] = move_steps;

    move_motors_by_steps(steps_for_back);
    print_current_position();
    return;
  }

  // 모터 1개 제어
  int motor_num = target_text.toInt();

  if (motor_num < 1 || motor_num > 4) {
    Serial.println("Motor number error");
    Serial.println("Use motor number 1~4, F, B, or ALL");
    return;
  }

  int motor_index = motor_num - 1;

  long steps_for_one[MOTOR_COUNT] = {0, 0, 0, 0};
  steps_for_one[motor_index] = move_steps;

  move_motors_by_steps(steps_for_one);
  print_current_position();
}

// --------------------------------------------------------------------------------------------------
// 패킷 명령 처리
// --------------------------------------------------------------------------------------------------

void read_packet(String packet) {
  int start_pos = 0;

  if (packet.charAt(0) == '@') {
    start_pos = 1;
  }

  // 명령 개수 2자리 확인
  if (packet.length() < start_pos + 2) {
    Serial.println("ERR: packet too short");
    return;
  }

  if (!isDigit(packet.charAt(start_pos)) || !isDigit(packet.charAt(start_pos + 1))) {
    Serial.println("ERR: command count must be 2 digits");
    return;
  }

  int command_count = (packet.charAt(start_pos) - '0') * 10 + (packet.charAt(start_pos + 1) - '0');

  if (command_count < 1 || command_count > MAX_COMMAND_COUNT) {
    Serial.println("ERR: command count must be 01~06");
    return;
  }

  long goal_steps[MOTOR_COUNT] = {0, 0, 0, 0};
  bool goal_on[MOTOR_COUNT] = {false, false, false, false};

  int block_pos = start_pos + 2;

  // 명령블록 해석
  for (int i = 0; i < command_count; i++) {
    if (packet.length() < block_pos + 8) {
      Serial.println("ERR: command block too short");
      return;
    }

    String leg_text = packet.substring(block_pos, block_pos + 2);
    String action_code;
    String value_text;

    // RE는 2글자 명령
    if (packet.length() >= block_pos + 9 && packet.substring(block_pos + 2, block_pos + 4) == "RE") {
      action_code = "RE";
      value_text = packet.substring(block_pos + 4, block_pos + 9);
      block_pos += 9;
    }
    else {
      action_code = packet.substring(block_pos + 2, block_pos + 3);
      value_text = packet.substring(block_pos + 3, block_pos + 8);
      block_pos += 8;
    }

    int leg_num = get_leg_index(leg_text);

    if (leg_num < 0) {
      Serial.print("ERR: unknown leg ");
      Serial.println(leg_text);
      return;
    }

    bool ok = handle_command_block(leg_num, action_code, value_text, goal_steps, goal_on);

    if (!ok) {
      return;
    }
  }

  if (block_pos != packet.length()) {
    Serial.println("ERR: packet length mismatch");
    return;
  }

  run_move_list(goal_steps, goal_on);

  Serial.println("OK");
}

// --------------------------------------------------------------------------------------------------
// 패킷 명령블록 처리
// --------------------------------------------------------------------------------------------------

bool handle_command_block(int leg_num, String action_code, String value_text, long goal_steps[], bool goal_on[]){
  action_code.toUpperCase();

  // A = SET 기준 절대 각도 이동
  if (action_code == "A") {
    if (!check_signed_angle_5(value_text)) {
      Serial.println("ERR: angle value must be like +0090 or -0090");
      return false;
    }

    long angle = text_to_long(value_text);
    long step_value = angle_to_steps(angle);

    goal_steps[leg_num] = step_value;
    goal_on[leg_num] = true;

    return true;
  }

  // RE = 해당 다리 위치값만 0으로 리셋
  if (action_code == "RE") {
    if (value_text != "00000") {
      Serial.println("ERR: RE value must be 00000");
      return false;
    }

    current_steps[leg_num] = 0;
    last_dir[leg_num] = 0;
    step_pos[leg_num] = 0;

    Serial.print("RESET ");
    Serial.println(get_leg_name(leg_num));

    return true;
  }

  Serial.print("ERR: unknown action ");
  Serial.println(action_code);

  return false;
}

// --------------------------------------------------------------------------------------------------
// 패킷에서 준비된 이동 실행
// --------------------------------------------------------------------------------------------------

void run_move_list(long goal_steps[], bool goal_on[]) {
  long logical_steps[MOTOR_COUNT] = {0, 0, 0, 0};

  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    // A 명령: 목표 위치까지 이동
    if (goal_on[motor]) {
      logical_steps[motor] = goal_steps[motor] - current_steps[motor];
    }
  }

  move_motors_by_steps(logical_steps);
}

// --------------------------------------------------------------------------------------------------
// 여러 모터 동시 이동
// --------------------------------------------------------------------------------------------------

void move_motors_by_steps(long logical_steps[]) {
  long real_steps[MOTOR_COUNT];
  long real_abs[MOTOR_COUNT];
  long max_steps = 0;

  // 보정 포함 실제 이동량 계산
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    real_steps[motor] = get_real_steps_with_backlash(motor, logical_steps[motor]);

    if (real_steps[motor] < 0) {
      real_abs[motor] = -real_steps[motor];
    }
    else {
      real_abs[motor] = real_steps[motor];
    }

    if (real_abs[motor] > max_steps) {
      max_steps = real_abs[motor];
    }
  }

  if (max_steps == 0) {
    Serial.println("No motor movement");
    return;
  }

  // 스텝 수가 다른 모터도 동시에 움직이도록 처리
  for (long i = 0; i < max_steps; i++) {
    for (int motor = 0; motor < MOTOR_COUNT; motor++) {
      if (i < real_abs[motor]) {
        if (real_steps[motor] > 0) {
          step_one_motor(motor, 1);
        }
        else if (real_steps[motor] < 0) {
          step_one_motor(motor, -1);
        }
      }
    }

    delay(STEP_DELAY);
  }

  // 위치값은 보정값이 아니라 입력값 기준으로 저장
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    current_steps[motor] += logical_steps[motor];
  }

  stop_all_motors();
}

// --------------------------------------------------------------------------------------------------
// 방향 전환 보정
// --------------------------------------------------------------------------------------------------

long get_real_steps_with_backlash(int motor, long logical_steps) {
  if (logical_steps == 0) {
    return 0;
  }

  int new_dir = 1;

  if (logical_steps < 0) {
    new_dir = -1;
  }

  long real_steps = logical_steps;

  // 이전 방향과 반대 방향이면 보정값 추가
  if (last_dir[motor] != 0 && last_dir[motor] != new_dir) {
    long abs_steps;

    if (logical_steps < 0) {
      abs_steps = -logical_steps;
    }
    else {
      abs_steps = logical_steps;
    }

    long extra_steps = abs_steps / BACKLASH_DIV;

    if (new_dir > 0) {
      real_steps += extra_steps;
    }
    else {
      real_steps -= extra_steps;
    }

    long extra_angle = extra_steps * 360 / STEPS_PER_360;
  }

  last_dir[motor] = new_dir;

  return real_steps;
}

// --------------------------------------------------------------------------------------------------
// SET / RESET / POS
// --------------------------------------------------------------------------------------------------

void set_current_as_start() {
  // 지금 위치를 기준 0으로 저장
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    current_steps[motor] = 0;
    last_dir[motor] = 0;
  }
}

void reset_to_start_position() {
  long reset_steps[MOTOR_COUNT];

  // 현재 위치의 반대 방향으로 이동해서 0으로 복귀
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    reset_steps[motor] = -current_steps[motor];
  }

  move_motors_by_steps(reset_steps);

  // 복귀 후 논리 위치를 0으로 정리
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    current_steps[motor] = 0;
  }
}

void print_current_position() {
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    long angle = current_steps[motor] * 360 / STEPS_PER_360;
  }
}

// --------------------------------------------------------------------------------------------------
// 모터 1스텝 이동
// --------------------------------------------------------------------------------------------------

void step_one_motor(int motor_num, int direction) {
  // 모터별 방향 보정 적용
  direction = direction * motor_dir[motor_num];

  if (direction > 0) {
    step_pos[motor_num]++;

    if (step_pos[motor_num] >= 4) {
      step_pos[motor_num] = 0;
    }
  }
  else if (direction < 0) {
    if (step_pos[motor_num] == 0) {
      step_pos[motor_num] = 3;
    }
    else {
      step_pos[motor_num]--;
    }
  }
  else {
    return;
  }

  for (int pin = 0; pin < 4; pin++) {
    digitalWrite(
      motor_pins[motor_num][pin],
      step_pattern[step_pos[motor_num]][pin]
    );
  }
}

// --------------------------------------------------------------------------------------------------
// 모터 출력 끄기
// --------------------------------------------------------------------------------------------------

void stop_all_motors() {
  for (int motor = 0; motor < MOTOR_COUNT; motor++) {
    for (int pin = 0; pin < 4; pin++) {
      digitalWrite(motor_pins[motor][pin], LOW);
    }
  }
}

// --------------------------------------------------------------------------------------------------
// 각도 → 스텝 변환
// --------------------------------------------------------------------------------------------------

long angle_to_steps(long angle) {
  return angle * STEPS_PER_360 / 360;
}

// --------------------------------------------------------------------------------------------------
// 패킷 각도값 검사
// --------------------------------------------------------------------------------------------------

bool check_signed_angle_5(String text) {
  if (text.length() != 5) {
    return false;
  }

  char sign = text.charAt(0);

  if (sign != '+' && sign != '-') {
    return false;
  }

  for (int i = 1; i < 5; i++) {
    if (!isDigit(text.charAt(i))) {
      return false;
    }
  }

  return true;
}

// --------------------------------------------------------------------------------------------------
// 다리 이름 → 모터 번호 변환
// --------------------------------------------------------------------------------------------------

int get_leg_index(String leg_text) {
  leg_text.toUpperCase();

  if (leg_text == "LF") {
    return LF;
  }

  if (leg_text == "RF") {
    return RF;
  }

  if (leg_text == "LB") {
    return LB;
  }

  if (leg_text == "RB") {
    return RB;
  }

  return -1;
}

String get_leg_name(int leg_num) {
  if (leg_num == LF) {
    return "LF";
  }

  if (leg_num == RF) {
    return "RF";
  }

  if (leg_num == LB) {
    return "LB";
  }

  if (leg_num == RB) {
    return "RB";
  }

  return "UNKNOWN";
}

// --------------------------------------------------------------------------------------------------
// 기타 보조 함수
// --------------------------------------------------------------------------------------------------

long text_to_long(String text) {
  return text.toInt();
}