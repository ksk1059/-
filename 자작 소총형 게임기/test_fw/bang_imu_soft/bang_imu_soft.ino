// BNO085 진단 v2 — 소프트웨어 I2C(클럭 스트레칭 대기 지원)로 SHTP 패킷 읽기
// 하드웨어 I2C에서 바이트가 반복되던 문제가 사라지는지 확인용
#define SDA_ 8
#define SCL_ 9
#define INT_ 10
#define RST_ 11
#define ADDR 0x4A
#define HALF_US 4      // 반주기 4us ≈ 100kHz 근처

// ---- 오픈드레인 소프트 I2C ----
static inline void sdaLow()  { pinMode(SDA_, OUTPUT); digitalWrite(SDA_, LOW); }
static inline void sdaRel()  { pinMode(SDA_, INPUT_PULLUP); }
static inline void sclLow()  { pinMode(SCL_, OUTPUT); digitalWrite(SCL_, LOW); }
static bool sclRel() {           // SCL 놓고 센서가 놓아줄 때까지 대기(클럭 스트레칭)
  pinMode(SCL_, INPUT_PULLUP);
  uint32_t t = micros();
  while (!digitalRead(SCL_)) { if (micros() - t > 50000) return false; }
  return true;
}
static void dly() { delayMicroseconds(HALF_US); }
static void i2cStart() { sdaRel(); sclRel(); dly(); sdaLow(); dly(); sclLow(); dly(); }
static void i2cStop()  { sdaLow(); dly(); sclRel(); dly(); sdaRel(); dly(); }
static bool writeByte(uint8_t b) {
  for (int i = 7; i >= 0; i--) { if (b & (1 << i)) sdaRel(); else sdaLow(); dly(); sclRel(); dly(); sclLow(); }
  sdaRel(); dly(); sclRel(); dly(); bool ack = !digitalRead(SDA_); sclLow(); dly();
  return ack;
}
static uint8_t readByte(bool ack) {
  uint8_t b = 0; sdaRel();
  for (int i = 7; i >= 0; i--) { dly(); sclRel(); dly(); if (digitalRead(SDA_)) b |= (1 << i); sclLow(); }
  if (ack) sdaLow(); else sdaRel();
  dly(); sclRel(); dly(); sclLow(); sdaRel(); dly();
  return b;
}
bool softRead(uint8_t *buf, int n) {
  i2cStart();
  if (!writeByte((ADDR << 1) | 1)) { i2cStop(); return false; }
  for (int i = 0; i < n; i++) buf[i] = readByte(i < n - 1);
  i2cStop(); return true;
}
bool softWrite(const uint8_t *buf, int n) {
  i2cStart();
  if (!writeByte(ADDR << 1)) { i2cStop(); return false; }
  for (int i = 0; i < n; i++) if (!writeByte(buf[i])) { i2cStop(); return false; }
  i2cStop(); return true;
}
// ---- SHTP ----
bool readPacket(uint32_t waitMs) {
  uint32_t t = millis();
  while (digitalRead(INT_) && millis() - t < waitMs) delay(1);
  if (digitalRead(INT_)) { Serial.println("  (INT 안 내려옴)"); return false; }
  uint8_t h[4];
  if (!softRead(h, 4)) { Serial.println("  헤더 NACK"); return false; }
  uint16_t len = (h[0] | (h[1] << 8)) & 0x7FFF;
  Serial.printf("  헤더: %02X %02X %02X %02X -> len=%u ch=%u\n", h[0], h[1], h[2], h[3], len, h[2]);
  if (len < 4 || len == 0x7FFF) return false;
  static uint8_t p[300]; int n = min((int)len, 300);
  softRead(p, n);
  Serial.print("  데이터:"); for (int i = 0; i < min(n, 24); i++) Serial.printf(" %02X", p[i]); Serial.println();
  return true;
}
void diag() {
  Serial.println("\n=== BNO085 소프트 I2C 진단 ===");
  digitalWrite(RST_, LOW); delay(20); digitalWrite(RST_, HIGH);
  for (int i = 0; i < 3; i++) { Serial.printf(" 패킷 %d\n", i + 1); if (!readPacket(1000)) break; }
  uint8_t req[6] = {6, 0, 2, 0, 0xF9, 0};
  Serial.printf(" 제품ID 요청 쓰기: %s\n", softWrite(req, 6) ? "OK" : "NACK");
  for (int i = 0; i < 4; i++) { Serial.printf(" 응답 %d\n", i + 1); if (!readPacket(1000)) break; }
  Serial.println("=== 끝 (10초 후 반복) ===");
}
void setup() {
  Serial.begin(115200);
  pinMode(INT_, INPUT_PULLUP); pinMode(RST_, OUTPUT); digitalWrite(RST_, HIGH);
  sdaRel(); sclRel();
  delay(3000);
}
void loop() { diag(); delay(10000); }
