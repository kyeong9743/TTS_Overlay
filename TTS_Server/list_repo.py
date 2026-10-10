import requests

url = "https://huggingface.co/api/models/5Hyeons/StyleTTS2"
response = requests.get(url)
data = response.json()
siblings = data.get("siblings", [])
for s in siblings:
    print(s["rfilename"])
