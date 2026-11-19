"""Draws packaging/thunderstore/icon.png (256x256): "ULTRA" white over "RT" red, inside a diamond traced by a glowing ray
bouncing off the middle of each frame edge (white -> orange -> red, fading out before it closes).
Usage: python tools/icon/make_icon.py <VCR_OSD_MONO.ttf> <out.png>. Font: VCR OSD Mono (free); scripts/trailer-font.py
extracts a copy to bench/results/trailer/fonts."""
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops
S=1024; RED=(255,78,40); WHITE=(255,255,255)
def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def sc(c,k): return tuple(int(v*k) for v in c)
bg=Image.new("RGB",(S,S),(10,10,12))
glow=Image.new("RGB",(S,S)); g=ImageDraw.Draw(glow)
core=Image.new("RGB",(S,S)); c=ImageDraw.Draw(core)
def seg(p,q,ca,cb,w):
    n=60
    for i in range(n):
        t0,t1=i/n,(i+1)/n
        a=(p[0]+(q[0]-p[0])*t0,p[1]+(q[1]-p[1])*t0); b=(p[0]+(q[0]-p[0])*t1,p[1]+(q[1]-p[1])*t1)
        col=lerp(ca,cb,(t0+t1)/2)
        g.line([a,b],fill=col,width=w*4); c.line([a,b],fill=col,width=w)
m=0
L,T,R,B=(m,512),(512,m),(S-m,512),(512,S-m)
P=[(L[0]+200,512+200),L,T,R,B,(L[0]+260,512+212)]
P=P[1:]
P[-1]=(B[0]-(B[0]-L[0])*0.78, B[1]-(B[1]-L[1])*0.78)
C=[WHITE,(255,190,150),(255,120,70),RED,sc(RED,0.0)]
for i in range(len(P)-1): seg(P[i],P[i+1],C[i],C[i+1],12)
glow=glow.filter(ImageFilter.GaussianBlur(26))
im=ImageChops.add(ImageChops.add(bg,glow),core); d=ImageDraw.Draw(im)
f1=ImageFont.truetype(sys.argv[1],200); f2=ImageFont.truetype(sys.argv[1],270)
for txt,ff,col,yy in [("ULTRA",f1,WHITE,430),("RT",f2,RED,630)]:
    d.text((S/2+12,yy+12),txt,font=ff,fill=(0,0,0),anchor="mm"); d.text((S/2,yy),txt,font=ff,fill=col,anchor="mm")
im.resize((256,256),Image.LANCZOS).save(sys.argv[2])
