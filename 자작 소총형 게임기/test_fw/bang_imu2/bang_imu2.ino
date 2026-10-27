// bang 보드 BNO085 동작 확인 — roll / pitch / yaw (도) 출력
// 같은 폴더의 SparkFun BNO08x 라이브러리 사본 사용 (소프트웨어 I2C로 수정됨: ESP32 하드웨어 I2C와 BNO085 궁합 문제 회피)
// Tools: Board = ESP32S3 Dev Module, USB CDC On Boot = Enabled, 115200 baud
// 시리얼 플로터(Ctrl+Shift+L)로 보면 세 줄 그래프로 보임
#include <Wire.h>
#include "SparkFun_BNO08x_Arduino_Library.h"

#define PIN_SDA     8
#define PIN_SCL     9
#define BNO_INT     10
#define BNO_RST     11
#define BNO_ADDR    0x4A   // SA0가 GND(R8)라서 0x4A
#define PIN_LED     13

BNO08x imu;

void setReports() {
  // Game Rotation Vector: 자기장(나침반) 미사용 → 실내/금속 근처에서도 안정. 총 조준용으로 적합
  if (imu.enableGameRotationVector(10)) Serial.println("Game Rotation Vector ON (100Hz)");
  else Serial.println("리포트 설정 실패");
}

void setup() {
  Serial.begin(115200);
  uint32_t t0 = millis();
  while (!Serial && millis() - t0 < 4000) delay(10);
  pinMode(PIN_LED, OUTPUT);
  Serial.println("\n=== bang IMU 테스트 ===");

  bno08x_set_soft_i2c_pins(PIN_SDA, PIN_SCL);   // 소프트웨어 I2C 핀 지정
  bool ok = false;
  for (int i = 1; i <= 5 && !ok; i++) {
    Serial.printf("begin 시도 %d ... INT=%d\n", i, digitalRead(BNO_INT));
    ok = imu.begin(BNO_ADDR, Wire, BNO_INT, BNO_RST);
    if (!ok) delay(500);
  }
  if (!ok) {
    Serial.println("BNO085 응답 없음 (0x4A). 멈춤.");
    while (1) { digitalWrite(PIN_LED, !digitalRead(PIN_LED)); delay(100); }  // 빠른 깜빡임 = 실패
  }
  Serial.println("BNO085 찾음");
  setReports();
  Serial.println("roll,pitch,yaw");
}

void loop() {
  if (imu.wasReset()) { Serial.println("센서 리셋됨 → 재설정"); setReports(); }
  if (imu.getSensorEvent() && imu.getSensorEventID() == SENSOR_REPORTID_GAME_ROTATION_VECTOR) {
    static uint32_t last = 0;
    if (millis() - last >= 50) {           // 20Hz로 출력 (읽기 편하게)
      last = millis();
      float r = imu.getRoll()  * 180.0 / PI;
      float p = imu.getPitch() * 180.0 / PI;
      float y = imu.getYaw()   * 180.0 / PI;
      Serial.printf("%.1f,%.1f,%.1f\n", r, p, y);
      digitalWrite(PIN_LED, !digitalRead(PIN_LED));   // 데이터 들어올 때마다 깜빡임
    }
  }
}
