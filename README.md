# ImageSearch
<a href="https://gitee.com/masuit/ImageSearch"><img src="https://gitee.com/static/images/logo-black.svg" height="24"></a>
<a href="https://github.com/ldqk/ImageSearch"><img src="https://upload.wikimedia.org/wikipedia/commons/thumb/9/95/Font_Awesome_5_brands_github.svg/54px-Font_Awesome_5_brands_github.svg.png" height="24"><img src="https://upload.wikimedia.org/wikipedia/commons/thumb/2/29/GitHub_logo_2013.svg/128px-GitHub_logo_2013.svg.png" height="24"></a>

图片exif信息移除小工具和本地硬盘以图搜图案例Demo分享，灵感来源于[DuplicateCleaner](https://masuit.org/1776)，**千万级图片秒级检索**：   
<img width="1165" height="840" alt="image" src="https://github.com/user-attachments/assets/9f295f3b-3edf-4227-bbd8-a4b386b59251" />
<img width="1307" height="1040" alt="image" src="https://github.com/user-attachments/assets/68aefef0-b143-4385-a7f9-fb9dbcaf073d" />
<img width="1377" height="911" alt="image" src="https://github.com/user-attachments/assets/34a37f96-a665-43ef-a4c9-c4f3a63c8b0e" />


## 环境要求
开发环境：Visual Studio 2026  
运行时：.net10 desktop  

## 硬件要求
处理器：4核或更多  
内存：8GB或更多

## 特别说明
1. 如果电脑中安装有everything，软件会自动调取everything进行目录扫描，请确保要扫描的目录已经被everything索引，如果你想让软件不自动调取everything，把目录下的everything64.dll文件删掉即可
2. 软件不支持部分区域的图片检索，只能做相似检索
3. 相似度限定70是因为低于70的相似度肉眼看上去已经是完全不一样的图了
4. GIF检索只在GIF库内比对，不会匹配静态图片；视频检索按1秒/帧采样比对，视频内局部画面检索依赖ORB深度搜索
5. 便携模式：索引、配置等所有数据均保存在程序所在目录（临时文件在程序目录 temp\ 子目录），不写入其他盘；如需迁移直接整体拷贝程序文件夹
6. 视频检索功能依赖 ffmpeg/ffprobe：因体积超过 GitHub 单文件限制未随源码分发，请自行下载（如 https://www.gyan.dev/ffmpeg/builds/ 的 release essentials 版）并将 ffmpeg.exe、ffprobe.exe 放入程序目录 tools\ 下；缺失时仅视频索引不可用，图片/GIF检索不受影响
## Star趋势

<img src="https://starchart.cc/ldqk/ImageSearch.svg">

## 理论篇
https://segmentfault.com/a/1190000038308093

## 特别鸣谢
[Masuit.Tools](https://github.com/ldqk/Masuit.Tools)

## 本项目完全开源，以下链接的为盗版，若您在以下链接以及相关链接有任何的付费行为，请申请退款或向相关平台方投诉
https://www.chinapyg.com/forum.php?mod=viewthread&tid=162510  
https://download.csdn.net/download/china365love/92183755  
https://blog.csdn.net/china365love/article/details/153752532  
https://shop.owmei.com/
