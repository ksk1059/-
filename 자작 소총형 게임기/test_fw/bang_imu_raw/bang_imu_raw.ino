// BNO085 날것(SHTP) 진단 — 라이브러리 없이 패킷 헤더/채널을 찍어 센서 상태 확인
// 정상 예: 리셋 후 [ch0 len~276 (advertisement)] → [ch1 len 5 (reset complete)] → 제품ID 요청 후 [ch2 ... F8 응답]
#include <Wire.h>
#define SDA_ 8
#define SCL_ 9
#define INT_ 10
#define RST_ 11
#define ADDR 0x4A
uint8_t seq[6] = {0};

bool readPacket(uint32_t waitMs) {
  uint32_t t = millis();
  while (digitalRead(INT_) && millis() - t < waitMs) delay(1);
  if (digitalRead(INT_)) { Serial.println("  (INT 안 내려옴 - 패킷 없음)"); return false; }
  if (Wire.requestFrom(ADDR, 4) != 4) { Serial.println("  헤더 읽기 실패(NACK)"); return false; }
  uint8_t h[4]; for (int i = 0; i < 4; i++) h[i] = Wire.read();
  uint16_t len = (h[0] | (h[1] << 8)) & 0x7FFF;
  Serial.printf("  헤더: %02X %02X %02X %02X -> len=%u ch=%u seq=%u\n", h[0], h[1], h[2], h[3], len, h[2], h[3]);
  if (len == 0 || len == 0x7FFF) { Serial.println("  길이 이상 (0 또는 FFFF) → 데이터 없음/버스 이상"); return false; }
  // 본문 앞부분 최대 28바이트만 다시 읽어서 표시 (헤더 포함해서 다시 옴)
  int n = min((int)len, 32);
  Wire.requestFrom(ADDR, n);
  Serial.print("  데이터:");
  for (int i = 0; i < n && Wire.available(); i++) Serial.printf(" %02X", Wire.read());
  Serial.println();
  // 남은 부분은 버림 (32바이트씩 계속 읽기)
  int remain = len - n;
  while (remain > 0) { int k = min(remain + 4, 32); Wire.requestFrom(ADDR, k); while (Wire.available()) Wire.read(); remain -= (k - 4); }
  return true;
}

void setup() {
  Serial.begin(115200);
  uint32_t t0 = millis(); while (!Serial && millis() - t0 < 4000) delay(10);
  delay(500);
  Serial.println("\n=== BNO085 SHTP 진단 ===");
  pinMode(INT_, INPUT_PULLUP);
  pinMode(RST_, OUTPUT);
  Wire.begin(SDA_, SCL_, 100000);
  Wire.setTimeOut(100);

  Serial.println("[1] 하드웨어 리셋");
  digitalWrite(RST_, LOW); delay(20); digitalWrite(RST_, HIGH);
  Serial.println("[2] 리셋 직후 패킷 3개 읽기 (advertisement, reset complete 기대)");
  for (int i = 0; i < 3; i++) { Serial.printf(" 패킷 %d\n", i + 1); if (!readPacket(1000)) break; }

  Serial.println("[3] 제품 ID 요청 (ch2, 0xF9) 보내기");
  uint8_t req[6] = {6, 0, 2, seq[2]++, 0xF9, 0};
  Wire.beginTransmission(ADDR); Wire.write(req, 6);
  int e = Wire.endTransmission();
  Serial.printf("  쓰기 결과: %d (0이면 성공)\n", e);
  Serial.println("[4] 응답 읽기 (ch2 데이터 첫 바이트 F8 기대)");
  for (int i = 0; i < 4; i++) { Serial.printf(" 패킷 %d\n", i + 1); if (!readPacket(1000)) break; }
  Serial.println("=== 끝. 이 출력을 전부 복사해서 보내주세요 ===");
}
void loop() {}
