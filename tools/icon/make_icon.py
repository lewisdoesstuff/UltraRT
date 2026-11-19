"""Draws packaging/thunderstore/icon.png (256x256). Usage: python tools/icon/make_icon.py <VCR_OSD_MONO.ttf> <out.png>.
Font: VCR OSD Mono (Riciery Leal, free); scripts/trailer-font.py extracts a copy to bench/results/trailer/fonts."""
import sys
from PIL import Image, ImageDraw, ImageFont, ImageFilter, ImageChops
S=1024; RED=(255,78,40); WHITE=(255,255,255)
def lerp(a,b,t): return tuple(int(a[i]+(b[i]-a[i])*t) for i in range(3))
def sc(c,k): return tuple(int(v*k) for v in c)
bg=Image.new("RGB",(S,S),(10,10,12))
glow=Image.new("RGB",(S,S)); g=ImageDraw.Draw(glow)
core=Image.new("RGB",(S,S)); c=ImageDraw.Draw(core)
def seg(p,q,ca,cb,w,k=1.0):
    n=60
    for i in range(n):
        t0,t1=i/n,(i+1)/n
        a=(p[0]+(q[0]-p[0])*t0,p[1]+(q[1]-p[1])*t0); b=(p[0]+(q[0]-p[0])*t1,p[1]+(q[1]-p[1])*t1)
        col=sc(lerp(ca,cb,(t0+t1)/2),k)
        g.line([a,b],fill=col,width=w*4); c.line([a,b],fill=col,width=w)
fy=1004
A,B=(20,740),(470,fy)
C=(994, int(fy-(994-470)*(fy-740)/450))
D=(C[0]-130, int(C[1]-130*(fy-740)/450))
seg(A,B,WHITE,(255,170,130),14); seg(B,C,(255,170,130),RED,14)
seg(C,D,sc(RED,0.7),(0,0,0),10)
for h,col,k,r in [(B,(255,150,100),1,1),(C,RED,0.5,0.6)]:
    g.ellipse([h[0]-150*r,h[1]-30*r,h[0]+150*r,h[1]+30*r],fill=sc(col,0.6*k))
    g.ellipse([h[0]-45*r,h[1]-45*r,h[0]+45*r,h[1]+45*r],fill=sc(col,k))
    c.ellipse([h[0]-20*r,h[1]-20*r,h[0]+20*r,h[1]+20*r],fill=sc(WHITE,k))
glow=glow.filter(ImageFilter.GaussianBlur(28))
im=ImageChops.add(ImageChops.add(bg,glow),core)
d=ImageDraw.Draw(im)
f=ImageFont.truetype(sys.argv[1],430)
w_u=d.textlength("U",font=f); tw=w_u+d.textlength("RT",font=f); x0=(S-tw)/2; y=481
d.text((x0+16,y+16),"URT",font=f,fill=(0,0,0),anchor="lm")
d.text((x0,y),"U",font=f,fill=WHITE,anchor="lm")
d.text((x0+w_u,y),"RT",font=f,fill=RED,anchor="lm")
im.resize((256,256),Image.LANCZOS).save(sys.argv[2])
