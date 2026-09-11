"""프로젝트 루트 런처. 어디서 실행하든 동작하도록 작업 경로를 고정한다.

사용: python run.py  (또는 파일 직접 실행)
"""
import os
import sys

# 이 파일이 있는 폴더(프로젝트 루트)를 기준으로 고정
ROOT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, ROOT)   # import poppy / import ui 보장
os.chdir(ROOT)             # motions/ 등 상대경로 보장

from ui.app import main

if __name__ == "__main__":
    main()
