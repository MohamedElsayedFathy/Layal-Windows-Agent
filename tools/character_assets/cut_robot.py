from PIL import Image
import os
import numpy as np, cv2
im=Image.open('sheet.png').convert('RGB')
S=4
crop=im.crop((1040,1010,1190,1150)).resize((150*S,140*S),Image.LANCZOS)
rgb=np.array(crop).astype(np.float32)
H,W,_=rgb.shape
border=np.concatenate([rgb[:12].reshape(-1,3),rgb[-12:].reshape(-1,3),rgb[:,:12].reshape(-1,3),rgb[:,-12:].reshape(-1,3)])
bg=np.median(border,axis=0); print('bg',bg)
lum=cv2.cvtColor(rgb.astype(np.uint8),cv2.COLOR_RGB2GRAY).astype(np.float32)
lumb=cv2.GaussianBlur(lum,(0,0),2.0)
# solid parts: bright ring/fins/moon
solid=(lumb>118).astype(np.uint8)
# drop right-bottom plant & far bokeh: keep component(s) near centre only
n,lab,st,cen=cv2.connectedComponentsWithStats(solid)
keep=np.zeros_like(solid)
for i in range(1,n):
    x,y,w,h,area=st[i]
    if area>150 and abs(cen[i][0]-W/2)<W*0.42 and abs(cen[i][1]-H*0.55)<H*0.46: keep[lab==i]=1
solid=cv2.morphologyEx(keep,cv2.MORPH_CLOSE,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(25,25)))
cnts,_=cv2.findContours(solid,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
filled=np.zeros_like(solid); cv2.drawContours(filled,cnts,-1,1,-1)
dark=((lumb<85)&(filled==1)).astype(np.uint8)
dn,dl,dst,dc=cv2.connectedComponentsWithStats(dark)
big=1+int(np.argmax(dst[1:,cv2.CC_STAT_AREA]))
dm=(dl==big).astype(np.uint8)
dcn,_=cv2.findContours(dm,cv2.RETR_EXTERNAL,cv2.CHAIN_APPROX_SIMPLE)
screen_f=np.zeros_like(dm); cv2.drawContours(screen_f,dcn,-1,1,-1)
sy,sx=np.where(screen_f==1); print('screen bbox',sx.min(),sy.min(),sx.max(),sy.max(),'area',int(screen_f.sum()))
feat=((screen_f==1)&(dm==0)).astype(np.uint8)
feat=cv2.dilate(feat,np.ones((13,13),np.uint8))
feat&=cv2.erode(screen_f,np.ones((25,25),np.uint8))
feat=cv2.dilate(feat,np.ones((31,31),np.uint8))
feat&=cv2.erode(screen_f,np.ones((25,25),np.uint8))
good=((screen_f==1)&(feat==0)).astype(np.float32)
base=rgb.copy()
num=cv2.GaussianBlur(base*good[...,None],(0,0),28); den=cv2.GaussianBlur(good,(0,0),28)[...,None]
fill=num/np.maximum(den,1e-3)
fm=cv2.GaussianBlur(feat.astype(np.float32),(0,0),6)[...,None]
rgb2=base*(1-fm)+fill*fm
if os.environ.get('FACE'): rgb2=rgb
# alpha: solid (feathered) + glow
solid_a=cv2.GaussianBlur(filled.astype(np.float32),(0,0),1.6)
delta=np.clip(rgb2-bg,0,None); dmax=delta.max(axis=2)
near=cv2.dilate(filled,cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(61,61))).astype(np.float32)
near=cv2.GaussianBlur(near,(0,0),6)
glow_a=np.clip((dmax-6)/150.0,0,1)*near*0.85
alpha=np.maximum(solid_a,glow_a)
col_solid=rgb2
col_glow=np.clip(delta/np.maximum(dmax[...,None],1)*255,0,255)
w=solid_a[...,None]
col=col_solid*w+col_glow*(1-w)
out=np.dstack([col,alpha*255]).astype(np.uint8)
ys,xs=np.where(alpha>0.03); x0,x1,y0,y1=xs.min(),xs.max(),ys.min(),ys.max()
spr=Image.fromarray(out).crop((x0,y0,x1+1,y1+1)); spr.save('robot_face_4x.png' if os.environ.get('FACE') else 'robot_blank_4x.png')
scr=(sx.min()-x0,sy.min()-y0,sx.max()-sx.min(),sy.max()-sy.min()); print('sprite',spr.size,'screen rel',scr)
open('robot_meta.txt','w').write(f"{spr.size[0]} {spr.size[1]} {scr[0]} {scr[1]} {scr[2]} {scr[3]}")
prev=Image.new('RGB',(2*spr.width,spr.height))
for i,c in enumerate([(244,247,252),(20,28,48)]):
    b=Image.new('RGBA',spr.size,c+(255,)); b.alpha_composite(spr); prev.paste(b.convert('RGB'),(i*spr.width,0))
prev.save('prev_robot.png')
