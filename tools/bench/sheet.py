"""One labelled sheet of a scenario's final screenshots, in capture order, with each frame's mean luma printed.
python tools/bench/sheet.py <run dir> <scenario file prefix, e.g. 7-1_pose_1> <out.png> [columns]
The label is the mode (with its variants); _raw and burst frames are skipped."""
import sys,glob,os,re
from PIL import Image,ImageDraw
import numpy as np
d,prefix,out=sys.argv[1],sys.argv[2],sys.argv[3]
cols=int(sys.argv[4]) if len(sys.argv)>4 else 3
fs=sorted(f for f in glob.glob(os.path.join(d,prefix+"_*s_*.png")) if not f.endswith("_raw.png") and "burst" not in f)
fs.sort(key=os.path.getmtime)
W,H=800,450
rows=(len(fs)+cols-1)//cols
s=Image.new("RGB",(W*cols,H*rows))
dr=ImageDraw.Draw(s)
for i,f in enumerate(fs):
    im=Image.open(f).convert("RGB")
    a=np.asarray(im).astype(float)
    m=re.sub(r"^.*?s_","",os.path.basename(f)[len(prefix):])[:-4]
    print(i,m,"luma %.1f"%(a@[0.2126,0.7152,0.0722]).mean())
    x,y=(i%cols)*W,(i//cols)*H
    s.paste(im.resize((W,H)),(x,y))
    dr.rectangle((x,y,x+W,y+14),fill=(0,0,0)); dr.text((x+3,y+1),f"{i} {m}",fill=(255,255,0))
s.save(out)
