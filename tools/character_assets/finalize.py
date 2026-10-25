from PIL import Image, ImageFilter
import numpy as np, cv2, os
from rembg import remove, new_session
im=Image.open('sheet.png').convert('RGB')
CW,CH=384,392
OUT='/home/claude/Layal-Windows-Agent/src/Layal.Desktop/Assets/Characters'
os.makedirs(OUT,exist_ok=True)

def clean(rgba,min_area=250):
    a=np.array(rgba); al=a[...,3]; al[al<14]=0
    m=(al>0).astype(np.uint8); n,lab,st,_=cv2.connectedComponentsWithStats(m)
    keep=np.zeros_like(m)
    for i in range(1,n):
        if st[i,cv2.CC_STAT_AREA]>=min_area: keep[lab==i]=1
    a[...,3]=al*keep; return Image.fromarray(a)
def trim(img,thr=20):
    al=np.array(img)[...,3]; ys,xs=np.where(al>thr); return img.crop((xs.min(),ys.min(),xs.max()+1,ys.max()+1))
def edge_fade(img,bottom=0.28,side=0.07):
    a=np.array(img).astype(np.float32); h,w=a.shape[:2]
    fy=np.ones(h,np.float32); nb=int(h*bottom); fy[h-nb:]=np.linspace(1,0,nb)**1.3
    fx=np.ones(w,np.float32); ns=int(w*side); ramp=np.linspace(0,1,ns)**1.2; fx[:ns]=ramp; fx[w-ns:]=ramp[::-1]
    a[...,3]*=fy[:,None]*fx[None,:]; return Image.fromarray(a.astype(np.uint8))
def to_canvas(img,width,top=0):
    s=width/img.width; t=img.resize((width,int(img.height*s)),Image.LANCZOS)
    c=Image.new('RGBA',(CW,CH),(0,0,0,0)); c.alpha_composite(t,((CW-t.width)//2,top)); return c

# idle (seated bust)
idle=Image.open('layal_idle_raw.png'); idle=to_canvas(idle,CW)
idle.save(f'{OUT}/layal-idle.png',optimize=True)

anime=new_session('isnet-anime'); general=new_session('isnet-general-use')
tiles={'happy':((712,68,872,210),general),'curious':((892,68,1048,210),anime),'playful':((1068,68,1222,210),anime),
       'focused':((712,255,872,397),anime),'surprised':((892,255,1048,397),general),'calm':((1068,255,1222,397),anime)}
for k,(b,sess) in tiles.items():
    t=im.crop(b).resize(((b[2]-b[0])*3,(b[3]-b[1])*3),Image.LANCZOS)
    c=remove(t,session=sess)
    a=np.array(c).astype(np.float32); al=a[...,3]
    thr=170 if k=='surprised' else 70
    al=np.clip((al-thr)/(255-thr),0,1)*255
    if k=='surprised': al=cv2.GaussianBlur(al,(0,0),0.8)            # drop faint tile-background remnants
    m=(al>0).astype(np.uint8)
    n,lab,st,_=cv2.connectedComponentsWithStats(m)
    big=1+int(np.argmax(st[1:,cv2.CC_STAT_AREA])); al=al*(lab==big)   # keep only the character
    a[...,3]=al; c=Image.fromarray(a.astype(np.uint8)); c=trim(c); c=edge_fade(c)
    c=c.filter(ImageFilter.UnsharpMask(radius=1.2,percent=60,threshold=2))
    c=to_canvas(c,276,top=6)
    c.save(f'{OUT}/layal-{k}.png',optimize=True); print(k,c.size)

# robot
r=Image.open('robot_blank_4x.png'); w,h=r.size
r=r.resize((192,int(h*192/w)),Image.LANCZOS); r.save(f'{OUT}/layal-robot.png',optimize=True)
meta=[float(x) for x in open('robot_meta.txt').read().split()]
print('robot',r.size,'screen frac x,y,w,h =',round(meta[2]/meta[0],4),round(meta[3]/meta[1],4),round(meta[4]/meta[0],4),round(meta[5]/meta[1],4),'aspect',meta[1]/meta[0])
for f in sorted(os.listdir(OUT)): print(f,os.path.getsize(f'{OUT}/{f}')//1024,'KB')
