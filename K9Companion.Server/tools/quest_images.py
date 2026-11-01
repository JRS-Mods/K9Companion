import os, sys
from PIL import Image

# Scales the K9 quest pictures in E:\SPT Mod\Assets\Questline imgs to res/quests/<quest number>.jpg for the server mod.
# The game's own quest pictures are 314 x 177; these are made at twice that so they stay sharp on large screens.
# Run: python tools/quest_images.py [pictures folder]
src = sys.argv[1] if len(sys.argv) > 1 else r'E:\SPT Mod\Assets\Questline imgs'
out = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'res', 'quests')
SIZE = (628, 354)
PICTURES = {
    1: "Quest 1 img.png",               # A Man's Best Friend
    4: "Quest 2 img.png",               # The Missing Unit
    5: "Quest 3 img.png",               # Trust Is Earned
    8: "Quest 4 img.png",               # Teeth and Steel
    10: "Quest 5 img.png",              # The Last Handler
    17: "Daily rations.png",
    18: "Safe Return.png",
    19: "Supply Run.png",
    20: "Working partnership.png",
    21: "The pack.png",
}

os.makedirs(out, exist_ok=True)
for quest, name in PICTURES.items():
    path = os.path.join(src, name)
    if not os.path.exists(path):
        print('missing', path); continue
    image = Image.open(path).convert('RGB')
    # Crop to the game's shape (centred) before scaling, so nothing is stretched.
    w, h = image.size
    want = SIZE[0] / SIZE[1]
    if w / h > want: cut = int((w - h * want) / 2); image = image.crop((cut, 0, w - cut, h))
    elif w / h < want: cut = int((h - w / want) / 2); image = image.crop((0, cut, w, h - cut))
    image = image.resize(SIZE, Image.LANCZOS)
    target = os.path.join(out, f'{quest}.jpg')
    image.save(target, quality=90, optimize=True)
    print('wrote', target, os.path.getsize(target) // 1024, 'KB')
