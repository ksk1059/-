# 돌쇠

행사장 대화 AI. 완전 오프라인, CPU 전용.

## 환경
- venv: `C:\dolswe_env` (한글 경로서 MeCab 깨져 ASCII로 분리). python: `C:\dolswe_env\Scripts\python.exe`.
- 의존성 설치/재현: `docs/superpowers/setup_env.ps1` 실행 후 `docs/superpowers/melo-patches.md` 패치 적용.
- Ollama 모델: `ollama pull exaone3.5:2.4b` / `ollama pull moondream`.
- 최초 1회 MeloTTS KR 모델 + kykim BERT가 HF에서 캐시됨(이후 오프라인).

## 실행
```bash
C:\dolswe_env\Scripts\python.exe -m dolswe.app
```

## 테스트
```bash
C:\dolswe_env\Scripts\python.exe -m pytest
```
