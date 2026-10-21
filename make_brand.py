"""Generate FlightGuard's original vector mark and Windows icons. Requires Pillow."""
from pathlib import Path
from PIL import Image,ImageDraw,ImageFont
root=Path(__file__).resolve().parent
assets=root/'Assets';assets.mkdir(exist_ok=True)
shield=[(32,3),(57,13),(55,37),(52,45),(44,53),(32,61),(20,53),(12,45),(9,37),(7,13)]
plane=[(30,14),(34,14),(37,29),(49,37),(49,42),(35,36),(35,47),(41,51),(41,54),(32,51),(23,54),(23,51),(29,47),(29,36),(15,42),(15,37),(27,29)]
svg='''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64"><title>FlightGuard shield and aircraft logo</title><path fill="#55dbc4" d="M32 3 57 13 55 37Q52 51 32 61 12 51 9 37L7 13Z"/><path fill="#092329" d="M30 14H34L37 29 49 37V42L35 36V47L41 51V54L32 51 23 54V51L29 47V36L15 42V37L27 29Z"/></svg>'''
(assets/'flightguard-logo.svg').write_text(svg,encoding='utf-8')
image=Image.new('RGBA',(1024,1024));d=ImageDraw.Draw(image)
for points,color in [(shield,'#55dbc4'),(plane,'#092329')]:d.polygon([(x*16,y*16) for x,y in points],fill=color)
image.resize((512,512),Image.Resampling.LANCZOS).save(assets/'flightguard-logo.png')
image.resize((256,256),Image.Resampling.LANCZOS).save(root/'flightguard.ico',sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
banner=Image.new('RGB',(1280,400),'#09111e');banner.paste(image.resize((280,280),Image.Resampling.LANCZOS),(65,60),image.resize((280,280),Image.Resampling.LANCZOS))
d=ImageDraw.Draw(banner)
def font(size,bold=False):
 try:return ImageFont.truetype('C:/Windows/Fonts/'+('segoeuib.ttf' if bold else 'segoeui.ttf'),size)
 except OSError:return ImageFont.load_default()
d.text((390,75),'FLIGHTGUARD',font=font(66,True),fill='#e8eff7')
d.text((395,170),'FROM THE GATE TO THE LAST CALLOUT',font=font(24),fill='#55dbc4')
d.text((395,235),'Infinite Flight Community  /  sadiq_ibraheem',font=font(25),fill='#a0b8d0')
d.text((395,285),'Windows companion  •  Recorded audio  •  Local Connect',font=font(21),fill='#8198b2')
banner.save(assets/'flightguard-banner.png')
print('Original SVG, PNG, ICO and banner generated')
