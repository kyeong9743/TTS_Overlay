# RTX 50 시리즈(Blackwell, compute capability sm_120)는 CUDA 11.8 / PyTorch 2.1에서
# 지원되지 않음. 그 조합은 커널 실행을 거부하거나("no kernel image is available for
# execution on the device") 조용히 폴백해서 RTX 5070을 구동하지 못함.
# 따라서 CUDA 12.8 + PyTorch 2.7 베이스를 사용.
FROM pytorch/pytorch:2.7.0-cuda12.8-cudnn9-runtime

# 시스템 의존성 설치
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y \
    build-essential \
    curl \
    default-jdk \
    espeak-ng \
    ffmpeg \
    git \
    libsndfile1 \
    mecab \
    libmecab-dev \
    mecab-ipadic-utf8 \
    && rm -rf /var/lib/apt/lists/*

# 작업 디렉토리 설정
WORKDIR /app

# Vocos를 지원하는 5Hyeons 저장소를 클론.
# 이 프로젝트가 동작을 확인한 커밋(2025-03-24)으로 고정함. 고정하지 않으면 빌드하는 시점의 최신 코드를 받게 되어,
# 원본 저장소가 바뀌었을 때 server.py와 맞지 않을 수 있음.
ARG STYLETTS2_VOCOS_COMMIT=14d97de72723f6326b99fecfa713ec7ffe30844c
RUN git clone https://github.com/5Hyeons/StyleTTS2-Vocos.git . \
    && git checkout ${STYLETTS2_VOCOS_COMMIT}

# Python 의존성 설치
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# 직접 작성한 서버, 다운로드 스크립트 복사
COPY server.py .
COPY download_model.py .

# FastAPI 포트 개방
EXPOSE 10000

# 서버 실행 (docker-compose에서 덮어씀)
CMD ["uvicorn", "server:app", "--host", "0.0.0.0", "--port", "10000"]
