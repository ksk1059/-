const logo = document.getElementById("logo");
const ripple = document.getElementById("ripple");
const botEl = document.getElementById("bot");
const userEl = document.getElementById("user");
const thinkingEl = document.getElementById("thinking");
let userTimer = null;

window.dolswe = {
  setThinking(on) { thinkingEl.classList.toggle("on", !!on); },
  setBotCaption(text) { botEl.textContent = text ? "돌쇠: " + text : ""; },
  setUserCaption(text) {
    userEl.textContent = text ? "너: " + text : "";
    if (userTimer) clearTimeout(userTimer);
    userTimer = setTimeout(() => { userEl.textContent = ""; }, 3000);
  },
  setAmplitude(level) {
    const s = 1 + Math.min(1, level) * 0.18;
    logo.style.transform = `scale(${s})`;
  },
  setSpeaking(on) {
    ripple.classList.toggle("on", !!on);
    logo.classList.toggle("idle", !on);
    if (!on) logo.style.transform = "";
  },
};

document.getElementById("inputbar").addEventListener("submit", (e) => {
  e.preventDefault();
  const input = document.getElementById("text");
  const t = input.value.trim();
  if (!t) return;
  input.value = "";
  window.dolswe.setUserCaption(t);
  if (window.pywebview) window.pywebview.api.on_user_text(t);
});

// 종료: ESC 키 또는 우상단 ✕ 버튼
function quitApp() {
  if (window.pywebview) window.pywebview.api.quit();
}
document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") quitApp();
});
document.getElementById("quit").addEventListener("click", quitApp);

// 마이크 on/off 토글
const micBtn = document.getElementById("mic");
function renderMic(on) {
  micBtn.textContent = on ? "🎤 ON" : "🔇 OFF";
  micBtn.classList.toggle("off", !on);
}
micBtn.addEventListener("click", async () => {
  if (!window.pywebview) return;
  const on = await window.pywebview.api.toggle_mic();
  renderMic(on);
});
