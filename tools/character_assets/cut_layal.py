from PIL import Image, ImageFilter
import numpy as np, cv2
from rembg import remove, new_session
sess=new_session('isnet-anime')
im=Image.open('sheet.png').convert('RGB')

def clean(rgba,min_area=250):
    a=np.array(rgba); al=a[...,3]
    al[al<14]=0
    m=(al>0).astype(np.uint8)
    n,lab,st,_=cv2.connectedComponentsWithStats(m)
    keep=np.zeros_like(m)
    for i in range(1,n):
        if st[i,cv2.CC_STAT_AREA]>=min_area: keep[lab==i]=1
    a[...,3]=al*keep
    return Image.fromarray(a)

def bbox(img,thr=20):
    al=np.array(img)[...,3]; ys,xs=np.where(al>thr); return xs.min(),ys.min(),xs.max()+1,ys.max()+1

# ---- idle: seated bust, cut at y=640 of the crop with soft fade
big=remove(im.crop((20,100,700,940)),session=sess)
big=big.crop((0,0,680,650))
a=np.array(big).astype(np.float32)
fade=np.ones(650,np.float32); fade[560:]=np.linspace(1,0,90)
a[...,3]*=fade[:,None]
# remove the plant (bottom-left) and stone ledge remnants by region
a[430:,0:120,3]*= np.clip(1-(np.arange(430,650)[:,None]-430)/60,0,1)*0.0+0.0 if False else 1
idle=clean(Image.fromarray(a.astype(np.uint8)))
idle=idle.crop(bbox(idle))
idle.save('layal_idle_raw.png'); print('idle',idle.size)

tiles={'happy':(712,68,872,210),'curious':(892,68,1048,210),'playful':(1068,68,1222,210),
       'focused':(712,255,872,397),'surprised':(892,255,1048,397),'calm':(1068,255,1222,397)}
out={}
for k,b in tiles.items():
    t=im.crop(b); t3=t.resize((t.width*3,t.height*3),Image.LANCZOS)
    c=remove(t3,session=sess); c=clean(c,min_area=1500); c=c.crop(bbox(c))
    c.save(f'layal_{k}_raw.png'); out[k]=c; print(k,c.size)
