"""Scoped physical regression: unique temporary files, exact readback, one batch."""
import argparse, hashlib, json, time, uuid
from pathlib import Path
import requests

parser = argparse.ArgumentParser()
parser.add_argument("--host", default="192.168.50.239")
parser.add_argument("--firmware")
parser.add_argument("--test", action="store_true")
args = parser.parse_args()
base = "http://" + args.host + "/"
def request(path, **kwargs):
    response = requests.post(base + path, timeout=600, **kwargs)
    response.raise_for_status()
    return response.json()
if args.firmware:
    with open(args.firmware,"rb") as firmware:
        print(request("api/firmware", files={"file":("firmware.bin",firmware,"application/octet-stream")}),flush=True)
    time.sleep(5)
print(requests.get(base+"api/device",timeout=10).json(),flush=True)
if args.test:
    folder = ".FlyingThumb-transfer-test-" + uuid.uuid4().hex
    print("Temporary test folder:",folder,flush=True)
    created = []
    batch = False
    try:
        print(request("api/files/begin"),flush=True); batch=True
        for index, size in enumerate([2906584,2633684,24027584]):
            name=folder+"/sample-"+str(index)+".bin"
            payload=(bytes(range(256))*((size+255)//256))[:size]
            start=time.monotonic()
            request("upload",params={"path":"/"+name,"restart":"0"},files={"file":("sample.bin",payload,"application/octet-stream")})
            created.append(name)
            response=requests.get(base+"api/download",params={"path":"/"+name},timeout=600)
            response.raise_for_status()
            if hashlib.sha256(response.content).digest()!=hashlib.sha256(payload).digest(): raise RuntimeError("Readback mismatch: "+name)
            print(json.dumps({"file":name,"bytes":size,"seconds":round(time.monotonic()-start,2),"exactReadback":"PASS"}),flush=True)
    finally:
        for name in created:
            print("cleanup",name,request("delete",params={"dir":"/"+name}),flush=True)
        if batch:
            try: print("commit",request("api/files/commit"),flush=True)
            except Exception as error: print("commit failed:",str(error),flush=True)
        print(requests.get(base+"api/device",timeout=10).json(),flush=True)
