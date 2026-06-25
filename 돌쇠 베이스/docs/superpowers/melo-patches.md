# MeloTTS 패치 (한국어 전용)

MeloTTS는 import 시 모든 지원 언어 모듈을 eager load한다. 일부(일본어/중국어)는 무거운 BERT·MeCab 의존성을 끌어와, 한국어만 쓰는 돌쇠에선 불필요한 실패/지연을 유발한다. 아래 2곳을 패치해 **한국어만** 로드한다.

설치/재설치(`setup_env.ps1`) 후 매번 재적용 필요. 대상 venv: `C:\dolswe_env`.

## 1. `melo/text/cleaner.py`

상단 import와 `language_module_map`을 한국어만으로 교체:

```python
from . import korean
from . import cleaned_text_to_sequence
import copy

# 돌쇠: 한국어만 사용. 다른 언어 모듈은 import 시 무거운 BERT/사전 로드하므로 생략.
language_module_map = {'KR': korean}
```

## 2. `melo/text/__init__.py` 의 `get_bert()`

여러 언어 `*_bert` eager import를 한국어만으로 교체:

```python
def get_bert(norm_text, word2ph, language, device):
    # 돌쇠: 한국어만 사용. 다른 언어 *_bert는 import 시 무거운 의존성(예: japanese.py→MeCab) 끌어옴.
    from .korean import get_bert_feature as kr_bert

    lang_bert_func_map = {"KR": kr_bert}
    bert = lang_bert_func_map[language](norm_text, word2ph, device)
    return bert
```

## 배경

- venv가 한글 경로(`Desktop\돌쇠`)면 MeCab(C++)이 사전 경로를 못 읽어 깨짐 → venv를 `C:\dolswe_env`(ASCII)로 분리.
- 한국어 g2p: `g2pkk` → Windows선 `eunjeon`(번들 mecab-ko 사전) 사용.
- 한국어 BERT: `kykim/bert-kor-base` 토크나이저가 `fugashi`+`unidic-lite` 요구 → 설치됨(ASCII venv라 동작).
