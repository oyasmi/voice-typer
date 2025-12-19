#!/usr/bin/env python3
"""
创建 VoiceTyper 应用图标
需要安装: pip install Pillow
"""
import os

def create_icon():
    """创建简单的应用图标"""
    try:
        from PIL import Image, ImageDraw, ImageFont
    except ImportError:
        print("需要安装 Pillow: pip install Pillow")
        return False
    
    # 创建目录
    os.makedirs("assets", exist_ok=True)
    
    # 图标尺寸
    sizes = [16, 32, 64, 128, 256, 512, 1024]
    images = []
    
    for size in sizes:
        # 创建图像
        img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
        draw = ImageDraw.Draw(img)
        
        # 画圆形背景
        margin = size // 10
        draw.ellipse(
            [margin, margin, size - margin, size - margin],
            fill=(52, 152, 219, 255)  # 蓝色
        )
        
        # 画麦克风图标（简化版）
        center = size // 2
        mic_width = size // 5
        mic_height = size // 3
        
        # 麦克风主体
        draw.rounded_rectangle(
            [
                center - mic_width // 2,
                center - mic_height // 2,
                center + mic_width // 2,
                center + mic_height // 2
            ],
            radius=mic_width // 2,
            fill=(255, 255, 255, 255)
        )
        
        # 麦克风支架
        stand_width = size // 20
        draw.rectangle(
            [
                center - stand_width // 2,
                center + mic_height // 2,
                center + stand_width // 2,
                center + mic_height // 2 + size // 8
            ],
            fill=(255, 255, 255, 255)
        )
        
        # 底座
        base_width = size // 4
        draw.rectangle(
            [
                center - base_width // 2,
                center + mic_height // 2 + size // 8,
                center + base_width // 2,
                center + mic_height // 2 + size // 8 + size // 20
            ],
            fill=(255, 255, 255, 255)
        )
        
        images.append(img)
    
    # 保存为 PNG（最大尺寸）
    images[-1].save("assets/icon.png")
    print("已创建: assets/icon.png")
    
    # 创建 .icns 文件（需要 iconutil）
    iconset_dir = "assets/icon.iconset"
    os.makedirs(iconset_dir, exist_ok=True)
    
    # 保存各尺寸
    icon_files = [
        (16, "icon_16x16.png"),
        (32, "icon_16x16@2x.png"),
        (32, "icon_32x32.png"),
        (64, "icon_32x32@2x.png"),
        (128, "icon_128x128.png"),
        (256, "icon_128x128@2x.png"),
        (256, "icon_256x256.png"),
        (512, "icon_256x256@2x.png"),
        (512, "icon_512x512.png"),
        (1024, "icon_512x512@2x.png"),
    ]
    
    for size, filename in icon_files:
        idx = sizes.index(size) if size in sizes else -1
        if idx >= 0:
            images[idx].save(os.path.join(iconset_dir, filename))
    
    # 使用 iconutil 转换
    result = os.system(f"iconutil -c icns {iconset_dir} -o assets/icon.icns")
    
    if result == 0:
        print("已创建: assets/icon.icns")
        # 清理临时文件
        import shutil
        shutil.rmtree(iconset_dir)
        return True
    else:
        print("警告: iconutil 转换失败，请手动创建 .icns 文件")
        return False


if __name__ == "__main__":
    create_icon()