// bang 보드 기능 점검 스케치 (ESP32-S3-WROOM-1, Arduino-ESP32)
// Tools 설정: Board = "ESP32S3 Dev Module", USB CDC On Boot = "Enabled"
// 시리얼 모니터 115200. 핀 번호는 PCB 배선 + ESP32-S3-WROOM-1 핀맵 기준.
#include <Wire.h>

const int PIN_TRIG     = 4;   // J2 방아쇠 (누르면 LOW)
const int PIN_MAG      = 5;   // J3 홀센서 (자석 오면 LOW)
const int PIN_BOLT     = 6;   // J4 노리쇠 (누르면 LOW)
const int PIN_RECENTER = 7;   // J5 리센터 (누르면 LOW)
const int PIN_SDA      = 8;   // BNO085 I2C
const int PIN_SCL      = 9;
const int PIN_IMU_INT  = 10;
const int PIN_IMU_RST  = 11;  // HIGH = 동작
const int PIN_MOTOR    = 12;  // 진동모터 PWM (Q1 게이트)
const int PIN_LED      = 13;  // D2 빨강 LED
const int BOOT_BTN     = 0;   // SW2 (누르면 LOW) -> 모터 테스트 버튼으로 사용

const int MOTOR_DUTY = 150;   // 0~255. 3V 모터를 5V로 돌리므로 약 59%로 제한

void i2cScan() {
  Serial.println("\n[I2C 스캔] BNO085는 0x4A가 떠야 정상");
  int found = 0;
  for (uint8_t a = 1; a < 127; a++) {
    Wire.beginTransmission(a);
    if (Wire.endTransmission() == 0) { Serial.printf("  발견: 0x%02X\n", a); found++; }
  }
  if (!found) Serial.println("  아무것도 없음 -> BNO085 납땜/전원/RST 확인");
}

void setup() {
  Serial.begin(115200);
  delay(1500);
  Serial.println("\n=== bang 보드 점검 시작 ===");

  pinMode(PIN_LED, OUTPUT);
  pinMode(PIN_MOTOR, OUTPUT); digitalWrite(PIN_MOTOR, LOW);
  pinMode(PIN_TRIG, INPUT); pinMode(PIN_MAG, INPUT);          // 보드에 10k 풀업 있음
  pinMode(PIN_BOLT, INPUT); pinMode(PIN_RECENTER, INPUT);
  pinMode(PIN_IMU_INT, INPUT_PULLUP);
  pinMode(BOOT_BTN, INPUT_PULLUP);

  // BNO085 리셋 해제
  pinMode(PIN_IMU_RST, OUTPUT);
  digitalWrite(PIN_IMU_RST, LOW);  delay(10);
  digitalWrite(PIN_IMU_RST, HIGH); delay(300);

  Wire.begin(PIN_SDA, PIN_SCL, 100000);
  i2cScan();
  Serial.println("\n입력 상태는 바뀔 때마다 출력. BOOT(SW2) 누르고 있으면 진동모터 동작.");
  Serial.println("시리얼에 's' 입력하면 I2C 재스캔.\n");
}

int last[5] = {-1,-1,-1,-1,-1};
const char* names[5] = {"TRIG(방아쇠)", "MAG(탄창)", "BOLT(노리쇠)", "RECENTER", "IMU_INT"};
const int pins[5] = {PIN_TRIG, PIN_MAG, PIN_BOLT, PIN_RECENTER, PIN_IMU_INT};

void loop() {
  static uint32_t t = 0;
  if (millis() - t > 500) { t = millis(); digitalWrite(PIN_LED, !digitalRead(PIN_LED)); }  // LED 깜빡임

  for (int i = 0; i < 5; i++) {
    int v = digitalRead(pins[i]);
    if (v != last[i]) {
      Serial.printf("%-14s : %s\n", names[i], v ? "HIGH (안 눌림/자석 없음)" : "LOW  (눌림/자석 감지)");
      last[i] = v;
    }
  }

  static bool motorOn = false;
  bool want = digitalRead(BOOT_BTN) == LOW;
  if (want != motorOn) {
    motorOn = want;
    analogWrite(PIN_MOTOR, motorOn ? MOTOR_DUTY : 0);
    Serial.println(motorOn ? "MOTOR ON" : "MOTOR OFF");
  }

  if (Serial.available() && Serial.read() == 's') i2cScan();
  delay(10);
}
