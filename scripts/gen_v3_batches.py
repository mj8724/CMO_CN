#!/usr/bin/env python3
"""按当前真实剩余缺口生成 v3 精修批次（每类一套 instructions）。"""

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
GAPS = ROOT / "build" / "residue_gaps.json"

BASE = [
    "返回 JSON 对象 {en: zh}，只输出 JSON。",
    "old 是历史译文（多为未翻或半译），仅供语境，禁止照抄。",
    "译文完整通顺，不残留普通英文单词（型号/编号/代号除外）。",
]

RULES = {
    "sensor": [
        "把每个 en（雷达/声呐/电子战/光电/通信设备）译成规范简体中文军事术语。",
        "型号/编号原样保留（AN/SPS-49、MR-123、MGK-500、Type 2087）。",
        "北约绰号按权威通译意译：Sheet Bend=单结、Front Door=前门、Bass Tilt=低音倾斜、"
        "Top Sail=顶帆、Shark Gill=鲨鱼鳃、Ox Yoke=牛轭、Band Stand=乐台、Spoon Rest=匙架、"
        "Knife Rest=刀架、Tall King=高王、Bar Lock=条锁、Big Back=大背、Squat Eye=矮眼、"
        "Flat Face=平脸、Low Blow=低吹、Straight Flush=直闪、Fire Can=火罐、Gun Dish=炮盘、"
        "Head Net=头网、Slot Back=槽背、Top Plate=顶板、Front Dome=前罩、Tall Rack=高架、"
        "Square Tie=方结、Sun Visor=遮阳板、Steel Work=钢铁厂、Box Spring=箱簧、"
        "Land Roll=陆滚、Hot Shot=热射、Wet Eye=湿眼、Mushroom=蘑菇、Trash Can=垃圾桶、"
        "Palm Frond=棕榈叶、Soft Ball=软球、Cheese Board=奶酪板、Stag Hoof=鹿蹄、"
        "Scan Fix=定扫、Cage Flask=笼瓶、Tin Trap=锡罐、Snoop Tray=窥探盘、"
        "Clam Pipe=蛤管、Hen House=鸡舍、Fly Trap=捕蝇器、Wine Glass=酒杯、"
        "Sprat Star=黍鲱星、Grid Wheel=格轮、Prim Wheel=普里姆轮、Mad Hack=疯砍、"
        "Monument=纪念碑、Cross Forks=十字叉、Cross Round=十字圆、Hair Bow=发弓、"
        "Hair Net=发网、Strut Pair=撑杆对、Strut Curve=撑杆曲线、Fire Iron=拨火棍、"
        "Plank Shave=刨板、High Lark=高云雀、High Lune=高月、Jay Bird=松鸦、"
        "Sword Fish=剑鱼、Ox Tail=牛尾、Puff Ball=马勃、Lamb Tail=羔尾、"
        "Top Pair=顶对、Flash Dance=闪舞、Barrier=屏障、IBIS=朱鹭、Sproot=斯普鲁特。",
        "功能词：Search=搜索、Track=跟踪、FCR=火控雷达、TIR=目标照射雷达、Warning=告警、"
        "RWR=雷达告警、DECM=防御电子对抗、OECM=攻势电子对抗、ESM=电子支援措施、"
        "ELINT=电子情报、COMINT=通信情报、SIGINT=信号情报、FLIR=前视红外、"
        "IRST=红外搜索跟踪、Sonar=声呐、Radar=雷达、Active=主动、Passive=被动、"
        "Towed Array=拖曳线列阵、VDS=可变深度声呐、AESA=有源相控阵、PESA=无源相控阵、"
        "MAWS=导弹逼近告警系统、DIRCM=定向红外对抗、LAMDIS=激光制导导弹、"
        "Datalink=数据链、SATCOM=卫星通信、IFF=敌我识别、MW=中波、SW=短波、MHz=兆赫、"
        "LLTV=微光电视、Camera=摄像机、Designator=指示器、Rangefinder=测距仪、"
        "Laser=激光、TV=电视、EO=光电、IR=红外、RF=射频、EW=电子战、"
        "Jammer=干扰机、Decoy=诱饵、Interceptor=拦截、Transceiver=收发机、"
        "Radio=电台、UHF=特高频、VHF=甚高频、HF=高频、Copy=仿制型、Mod=改进型、"
        "Hull=舰壳声呐、Mine Avoidance=水雷规避、Anti-Saboteur=反蛙人、"
        "Side Scan=侧扫、Mine Sweep=扫雷具、Acquisition=截获、Turret=炮塔、"
        "Fire Control=火控、Director=指挥仪、Illumination=照射、CW=连续波、"
        "MTI=动目标显示、ISAR=逆合成孔径雷达、SAR=合成孔径雷达、"
        "TAR=目标捕获雷达、PAR=相控阵雷达、HPI=大功率照射器、"
        "WAAS=广域监视系统、DAS=防御辅助系统、IRDS=红外探测系统、"
        "Outboard=舷外、NGJ=下一代干扰机、TSS=目标瞄准系统、"
        "LPAR=大型相控阵雷达、SIAR=综合脉冲孔径雷达、SIAR=综合脉冲孔径雷达、"
        "ROTHR=天波超视距雷达、Navigation=导航、Communications=通信、"
        "Transmiter=发射机、Receiver=接收机、Acoustic=声学、Intercept=侦听。",
        "纯型号/代号（无普通英文单词）保留原样；方括号内也翻译，保留方括号。",
    ],
    "loadout": [
        "把每个 en（挂载方案/弹舱/弹药箱）译成规范简体中文。",
        "型号/编号原样保留（Mk82、500lb、3-inch、AN/ALE-40、GBU-31(V)1/B）。",
        "弹种：LDGP=低阻通用炸弹、GPB=通用炸弹、Strategic Bomb=战略炸弹、Nuclear=核弹、"
        "Chaff=箔条、Flare=红外诱饵、Rockets=火箭弹、Salvo=齐射、Burst=连发点射、rnds=发、"
        "CBU=集束炸弹、LGB=激光制导炸弹、JDAM=联合直接攻击弹药、"
        "WCMD=风偏修正弹药布撒器、JSOW=联合防区外武器、"
        "JASSM=联合空对地防区外导弹、MALD=微型空射诱饵、"
        "ALCM=空射巡航导弹、ATGM=反坦克导弹、ASM=空舰导弹、ARM=反辐射导弹、"
        "AAM=空空导弹、SAM=防空导弹、SSM=地对地导弹、"
        "Torpedo=鱼雷、Mine=水雷、Depth Charge=深水炸弹、"
        "Incendiary=燃烧弹、Illumination=照明弹、Practice=练习弹、"
        "Inert=惰性弹、Training=训练弹、Smoke=烟幕弹、"
        "Submunition=子弹药、Canister=弹筒、Cartridge=弹筒、"
        "Grenade=榴弹、Bomblet=子弹药、Cluster=集束、"
        "Fuel Air Explosive=燃料空气炸弹、Superfrag=超级破片、"
        "Rocket=火箭弹、Missile=导弹、Bomb=炸弹、Gun=机炮、Pod=吊舱。",
        "吊舱/光电：Litening=利特宁、Sniper=狙击手、LANTIRN=蓝丁、"
        "Damocles=达摩克利斯、ATFLIR=先进瞄准前视红外、Talios=塔利奥斯、"
        "Sharpshooter=神枪手、Night Owl=夜鸮、FLIR=前视红外、LRMTS=激光测距寻标仪、"
        "EOTS=光电瞄准系统、TADS=目标截获指示系统、PNVS=飞行员夜视系统、"
        "NVG=夜视镜、Targeting Pod=瞄准吊舱、Recon Pod=侦察吊舱、"
        "Jamming Pod=干扰吊舱、ECM Pod=电子对抗吊舱、"
        "Barax=巴拉克斯、Phimat=菲马特、PAJ-FA=PAJ-FA。",
        "重量/口径：500lb=500磅、1000lb=1000磅、3-inch=3英寸、"
        "70mm=70毫米、2000lb=2000磅；当量 200kT=20万吨、1.2mT=120万吨、"
        "340kT=34万吨、550kT=55万吨、750kT=75万吨、1MT=100万吨。",
        "挂架：Pylon=挂架、Rack=挂架、Bomb Rack=炸弹挂架、"
        "I-beam Pylon=工字梁挂架、Slung Load=吊挂载荷、"
        "External Fuel=外挂副油箱、Internal Only=仅内部弹舱、"
        "Wing Pylons Only=仅翼下挂架、Buddy Illumination=友机照射、"
        "Drogue=锥套、Boom=硬管、Tank=副油箱、Drop Tank=副油箱。",
        "任务：CAS=近距空中支援、RESCAP=救援战斗空中巡逻、"
        "BarCAP=屏障战斗空中巡逻、MiGCAP=米格战斗空中巡逻、"
        "FAC=前进空中管制、SEAD=压制敌防空、Anti-Ship Role=反舰任务、"
        "Standard War Loadout=标准作战挂载、"
        "Eldorado Canyon=黄金峡谷行动、Operation Bolo=波洛行动。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "facility": [
        "把每个 en（设施/地面部队）译成规范简体中文。型号/编号原样保留。",
        "设施：Pill Box=地堡、Marker=标记、Observation Post=观察哨、Building=建筑、"
        "Structure=结构设施、Bunker=掩体、Bridge=桥梁、Transformer=变压器、"
        "Turbine=涡轮机、Control Tower=管制塔、Coastal Arty Bty=岸防炮兵连、"
        "SAM Bty=地空导弹连、Inf Sec=步兵班、Coastal Gun=岸防火炮、"
        "Casemate=炮座、Open Gun Well=露天炮位、Turret=炮塔、Radar=雷达站、"
        "Vehicle=车辆、Reveted=有掩体、Magazine=弹药库、Fort=要塞、"
        "Coastal Fort=岸防要塞、Dock=船坞、Shipyard=造船厂、"
        "Power Station=发电站、Pipeline=管道、Factory=工厂、Depot=仓库、"
        "Barracks=营房、Hospital=医院、Shelter=掩体、Silo=发射井、"
        "Pier=码头、Runway=跑道、Taxiway=滑行道、Apron=停机坪、"
        "Hangar=机库、Airfield=机场、Airbase=空军基地、"
        "Naval Base=海军基地、Port=港口、Harbor=港湾、"
        "Bridge=桥梁、Tunnel=隧道、Road=道路、Rail=铁路、"
        "Village=村庄、Town=城镇、City=城市、Farm=农场、"
        "Oil=石油、Gas=天然气、Fuel=燃料、Water=水、"
        "Electric=电力、Nuclear=核、Solar=太阳能、Wind=风能。",
        "编制：Bn=营、Bty=连、Coy=连、Plt=排、Sec=班、Sqn=中队、Regt=团、"
        "Bde=旅、Div=师、Mech=机械化、Arty=炮兵、Inf=步兵、Recce=侦察、"
        "Armor=装甲、MBT=主战坦克、APC=装甲输送车、IFV=步兵战车、"
        "MANPADS=便携式防空导弹、TEL=运输起竖发射车、MLRS=多管火箭炮、"
        "AAA=高射炮、AA=防空、AT=反坦克、Engineer=工兵、"
        "HQ=司令部、Command Post=指挥所、Supply=补给、Logistics=后勤、"
        "Maintenance=维修、Medical=医疗、Transport=运输、"
        "Air Defence=防空、Coastal=岸防、Garrison=卫戍、"
        "Battalion=营、Battery=连、Company=连、Platoon=排、Section=班、"
        "Squad=班、Team=组、Group=群、Regiment=团、Brigade=旅、Division=师、"
        "Corps=军、Army=集团军、Command=指挥部、Unit=部队、Force=部队。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "aircraft": [
        "把每个 en（飞机/潜艇）译成规范简体中文。型号/编号原样保留（MB.545、SA 313B、Mk.2）。",
        "飞机绰号意译：Barracuda=梭鱼、Mystere=神秘、Vautour=秃鹰、Alouette=云雀、"
        "Sabre=佩刀、Phantom=鬼怪、Corsair=海盗、Orion=猎户座、Hercules=大力神、"
        "Stratofortress=同温层堡垒、Skyraider=空中袭击者、Thunderjet=雷电喷气、"
        "Shooting Star=流星、Beverley=贝弗利、Hastings=黑斯廷斯、Falcon=战隼、"
        "Eagle=鹰、Tomcat=雄猫、Harrier=鹞、Greyhound=灰狗、Hawkeye=鹰眼、"
        "Viking=北欧海盗、Prowler=徘徊者、GlobalHawk=全球鹰、Predator=捕食者、"
        "Reaper=死神、Heron=苍鹭、Wing Loong=翼龙、Soar Dragon=翔龙、"
        "Kunlong=鲲龙、Ghost Bat=幽灵蝙蝠、Hammerhead=锤头、Wolverine=狼獾、"
        "Skeeter=蚊子、Mako=灰鲭鲨、Thrasher=嘲鸫、Torito=小公牛、"
        "Hoodlum=恶棍、Mitten=连指手套、Foxglove=毛地黄、SeaGuardian=海上卫士、"
        "GlobalEye=全球眼、Persuader=说服者、Kansan=堪萨斯人、Sumo=相扑、"
        "Mariner=水手、Mercury=水星、Nomad=游牧民、Kiowa=基奥瓦、"
        "Apache=阿帕奇、Black Hawk=黑鹰、Chinook=支奴干、"
        "Sea King=海王、Lynx=山猫、Puma=美洲狮、Gazelle=瞪羚、"
        "Alouette=云雀、Ecureuil=松鼠、Dauphin=海豚、"
        "Fennec=沙漠狐、Cougar=美洲狮、Caracal=狞猫、"
        "Bear=熊、Badger=獾、Bison=野牛、Backfire=逆火、"
        "Blackjack=海盗旗、Fulcrum=支点、Flanker=侧卫、"
        "Foxbat=狐蝠、Frogfoot=蛙足、Fencer=击剑手、"
        "Foxhound=猎狐犬、Hind=雌鹿、Hip=河马、Hokum=噱头、"
        "Havoc=浩劫、Hormone=激素、Helix=蜗牛、Haze=阴霾、"
        "May=五月、Mail=邮件、Moss=苔藓、Madcap=疯帽、"
        "Mainstay=主桅、Midas=迈达斯、Candid=耿直、Cargo=货物、"
        "Camembert=卡门贝尔、Colt=马驹、Cub=幼兽、Curl=卷发、"
        "Cab=驾驶室、Coke=焦炭、Cooker=炊具、Crate=板条箱、"
        "Creek=小溪、Cabin=客舱、Camel=骆驼、Canoe=独木舟、"
        "Careless=疏忽、Cart=大车、Cash=现金、Cat=猫、"
        "Chan=陈、Charlie=查理、Clam=蛤、Clank=叮当、"
        "Classic=经典、Cleat=楔子、Clod=土块、Clog=木鞋、"
        "Coach=马车、Coaler=运煤船、Coast=海岸、Cobra=眼镜蛇、"
        "Cod=鳕鱼、Cog=齿轮、Collier=运煤船、Condor=秃鹰、"
        "Coot=白骨顶、Cork=软木、Cossack=哥萨克、Crib=婴儿床、"
        "Crow=乌鸦、Cruiser=巡洋舰、Cuckoo=布谷鸟。",
        "潜艇级：Yankee=扬基级、Delta=德尔塔级、Typhoon=台风级、Kilo=基洛级、"
        "Oberon=奥伯龙级、Agosta=阿戈斯塔级、Whiskey=威士忌级、Foxtrot=狐步级、"
        "Los Angeles=洛杉矶级、Virginia=弗吉尼亚级、Seawolf=海狼级、Ohio=俄亥俄级、"
        "Charlie=查理级、Victor=维克托级、Alfa=阿尔法级、Papa=爸爸级、"
        "Oscar=奥斯卡级、Akula=阿库拉级、Sierra=塞拉级、"
        "Romeo=罗密欧级、Golf=高尔夫级、Hotel=旅馆级、"
        "Juliett=朱丽叶级、November=十一月级、Echo=回声级、"
        "Tango=探戈级、Bravo=布拉沃级、Quebec=魁北克级、"
        "Zulu=祖鲁级、Barracuda=梭鱼级、Rubis=红宝石级、"
        "Triomphant=可畏级、Astute=机敏级、Trafalgar=特拉法尔加级、"
        "Swiftsure=快捷级、Resolution=决心级、Vanguard=先锋级、"
        "Collins=柯林斯级、Type 209=209型、Type 214=214型、"
        "Gotland=哥特兰级、Sodermanland=南曼兰级、"
        "Kobben=科本级、Ula=乌拉级、Walrus=海象级、"
        "Sauro=萨乌罗级、Type 212=212型、Dolphin=海豚级、"
        "Yaonan=尧南级、Han=汉级、Xia=夏级、Jin=晋级、"
        "Shang=商级、Song=宋级、Ming=明级、Yuan=元级、"
        "Kilo=基洛级、Lada=拉达级、Borei=北风之神级、"
        "Yasen=亚森级、Severodvinsk=北德文斯克级、"
        "Lun=卢恩级、Sarov=萨罗夫级、Belgorod=别尔哥罗德号、"
        "Losharik=洛沙里克号、Pr. 系列按「NN型」（Pr.671=671型）。",
        "通用：UAV=无人机、UCAV=无人作战飞机、ROV=遥控潜航器、UUV=无人水下航行器、"
        "Glider=滑翔机、Airship=飞艇、Balloon=气球、"
        "Reconnaissance=侦察、Patrol=巡逻、Transport=运输、"
        "Tanker=加油机、Trainer=教练机、Fighter=战斗机、"
        "Bomber=轰炸机、Attack=攻击机、Helicopter=直升机、"
        "ASW=反潜、AEW=空中预警、EW=电子战、MPA=海上巡逻机、"
        "Copy=仿制型、Mod=改进型、Derivative=衍生型、Late=后期型。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "satellite": [
        "把每个 en（卫星）译成规范简体中文。型号/编号原样保留（KH-11A、FSW-0、Blk I）。",
        "美国侦察/预警：Key Hole=锁眼、Big Bird=大鸟、Hexagon=六边形、"
        "Kennan=凯南、Aquacade=水族馆、Chalet=小屋、Vortex=旋涡、"
        "Jumpseat=跳跃座椅、Misty=迷雾、Magnum=大酒瓶、Trumpet=小号、"
        "Mentor=门托、Mercury=水星、Quasar=类星体、Lacrosse=曲棍球、"
        "Onyx=缟玛瑙、Crystal=水晶、Improved Crystal=改进型水晶、"
        "Satellite Data System=卫星数据系统、SBIRS=天基红外系统、"
        "DSP=国防支援计划、GPS=全球定位系统、Navstar=导航星、"
        "Milstar=军事星、AEHF=先进极高频、WGS=宽带全球卫星、"
        "MUOS=移动用户目标系统、Iridium=铱星、Globalstar=全球星、"
        "Landsat=陆地卫星、Seasat=海洋卫星、Nimbus=雨云、"
        "TIROS=泰罗斯、GOES=戈斯、DMSP=国防气象卫星、"
        "NOAA=诺阿、TRMM=热带降雨测量、Terra=特拉、Aqua=阿卡、"
        "Aura=奥拉、CALIPSO=卡利普索、CloudSat=云卫星、"
        "ICESat=冰卫星、GRACE=格雷斯、Jason=贾森、"
        "Topex=托佩克斯、QuikSCAT=快速散射计、"
        "Coriolis=科里奥利、ADEOS=先进地球观测卫星、"
        "Ajisai=石楠、GOSAT=温室气体观测卫星、Himawari=向日葵、"
        "QZS=准天顶卫星、Michibiki=引路、ETS=工程试验卫星、"
        "ALOS=先进陆地观测卫星、Daichi=大地、"
        "COSMO-SkyMed=地中海天宇、SAR-Lupe=合成孔径雷达-放大镜、"
        "Helios=太阳神、Pleiades=昴星团、Spot=斯波特、"
        "Envisat=环境卫星、ERS=欧洲遥感卫星、Sentinel=哨兵、"
        "TerraSAR-X=陆地合成孔径雷达-X、TanDEM-X=串联-X、"
        "RISAT=雷达成像卫星、Cartosat=制图卫星、Oceansat=海洋卫星、"
        "IRNSS=印度区域导航卫星系统、NavIC=印度导航星座、"
        "Galileo=伽利略、Glonass=格洛纳斯、Beidou=北斗、"
        "Resurs=资源、Meteor=流星、Kosmos=宇宙、"
        "Yantar=琥珀、Tselina=处女地、Persona=角色、"
        "Bars=雪豹、US-P=US-P、US-PM=US-PM、"
        "EORSAT=电子侦察卫星、RORSAT=雷达海洋侦察卫星、"
        "Fanhui Shi Weixing=返回式卫星、FSW=返回式、"
        "Yaogan=遥感、Gaofen=高分、Tianlian=天链、"
        "Shijian=实践、Chinasat=中星、Fengyun=风云、"
        "Tiangong=天宫、Shenzhou=神舟、Chang'e=嫦娥、"
        "Yutu=玉兔、Tianwen=天问、Hongyan=鸿雁、"
        "Xingyun=行云、Jilin=吉林、Chuangxin=创新、"
        "Shiyan=试验、Lingqiao=灵巧、Jinping=锦屏、"
        "Tianyan=天眼、Beta=贝塔、Tau=陶、"
        "HySIS=高光谱成像卫星、Microdragon=微龙。",
        "卫星类别：Reconnaissance=侦察、Early Warning=预警、"
        "SIGINT=信号情报、ELINT=电子情报、COMINT=通信情报、"
        "Navigation=导航、Communication=通信、Weather=气象、"
        "Ocean Surveillance=海洋监视、Missile Warning=导弹预警、"
        "Imaging=成像、Radar=雷达、Optical=光学、Infrared=红外、"
        "Hyperspectral=高光谱、SAR=合成孔径雷达、EO=光电、"
        "GEO=地球同步轨道、LEO=低地球轨道、MEO=中地球轨道、"
        "HEO=高椭圆轨道、Constellation=星座、"
        "Experimental=试验、Technology=技术、Amateur=业余、"
        "Communication=通信、Broadcast=广播、Data Relay=数据中继。",
        "方括号内也翻译，保留方括号。纯型号保留原样；译文完整通顺。",
    ],
    "weapon": [
        "把每个 en（武器/战斗部）译成规范简体中文。型号/编号原样保留（UGM-27B、Mk4）。",
        "弹头：MRV=多弹头分导、MIRV=多弹头独立分导、Nuclear=核弹头、HE=高爆弹、"
        "HEAT=破甲弹、HECVT=高爆可变量引信、SAP=半穿甲、Frag=破片弹、"
        "Shaped Charge=聚能装药、Tandem-Charge=串联聚能、"
        "Penetrator=侵彻弹头、HGV=高超音速滑翔载具、RV=再入飞行器、"
        "Warhead=战斗部、Depth Charge=深水炸弹、Torpedo=鱼雷、"
        "Mine=水雷、Missile=导弹、Rocket=火箭弹、Bomb=炸弹、"
        "Cluster=集束、Incendiary=燃烧弹、Illumination=照明弹、"
        "Practice=练习弹、Inert=惰性弹、Training=训练弹、"
        "Smoke=烟幕弹、Chaff=箔条、Flare=红外诱饵、"
        "Submunition=子弹药、Canister=弹筒、Cartridge=弹筒、"
        "Salvo=齐射、Burst=连发点射、Bomblets=子弹药、"
        "Guided=制导、Unguided=非制导、Ballistic=弹道、"
        "Cruise=巡航、Anti-Ship=反舰、Anti-Tank=反坦克、"
        "Anti-Radiation=反辐射、Anti-Submarine=反潜、"
        "Surface-to-Air=地对空、Air-to-Air=空对空、"
        "Air-to-Surface=空对地、Surface-to-Surface=地对地、"
        "Submarine-Launched=潜射、Air-Launched=空射、"
        "Coastal=岸基、Shipborne=舰载、Vehicle=车载、"
        "Shoulder Fired=肩射、Man Portable=便携式、"
        "Dispenser=布撒器、Launcher=发射装置、Magazine=弹仓。",
        "武器绰号意译：Polaris=北极星、Minuteman=民兵、Squid=乌贼、"
        "Enot=浣熊、Bidder=投标者、Sego=赛果、Sandal=凉鞋、"
        "Scud=飞毛腿、Stiletto=匕首、Hwasong=火星、Rodong=劳动、"
        "Taepodong=大浦洞、Shahab=流星、Fateh=征服者、"
        "Sejil=塞吉尔、Zelzal=地震、Ghadr=加德尔、Qiam=起义、"
        "Sayyad=赛义德、Kumsong=金星、Bulsae=火鸟、"
        "Pukguksong=北极星、Iskander=伊斯坎德尔、Kalibr=口径、"
        "Zircon=锆石、Bulava=布拉瓦、Kanyon=峡谷、Topol=白杨、"
        "Satan=撒旦、Saber=军刀、Skiff=轻舟、Spanker=鞭策者、"
        "Stallion=种马、Starfish=海星、Sizzler=炙热、Stone=石头、"
        "Snark=蛇鲨、Trident=三叉戟、Sidewinder=响尾蛇、"
        "Sparrow=麻雀、Harpoon=鱼叉、Hellfire=地狱火、"
        "Paveway=宝石路、Maverick=小牛、Javelin=标枪、TOW=陶式、"
        "Stinger=毒刺、Igla=针、Kornet=短号、Metis=混血儿、"
        "Fagot=柴捆、Konkurs=竞赛、BrahMos=布拉莫斯、"
        "Exocet=飞鱼、Otomat=奥托马特、Penguin=企鹅、"
        "Tomahawk=战斧、ALCM=空射巡航导弹、ACM=先进巡航导弹、"
        "Patriot=爱国者、Buk=山毛榉、Tor=道尔、Pantsir=铠甲、"
        "Tunguska=通古斯卡、Iron Dome=铁穹、David's Sling=大卫投石索、"
        "Arrow=箭、THAAD=萨德、Aegis=宙斯盾、Standard=标准、"
        "ESSM=改进型海麻雀、Sea Sparrow=海麻雀、RAM=拉姆、"
        "Phalanx=密集阵、Goalkeeper=守门员、Kashtan=卡什坦、"
        "Meroka=梅罗卡、Millennium=千禧年、Otobreda=奥托布雷达、"
        "DARDO=达尔多、Gatling=加特林、Vulcan=火神、Minigun=迷你炮、"
        "Chain=链炮、Bushmaster=大毒蛇、Cannon=机炮、Gun=炮、"
        "Howitzer=榴弹炮、Mortar=迫击炮、Recoilless=无后坐力炮、"
        "MLRS=多管火箭炮、ATGM=反坦克导弹、MANPADS=便携式防空导弹、"
        "SAM=防空导弹、SSM=地对地导弹、ASM=空舰导弹、AAM=空空导弹、"
        "ARM=反辐射导弹、SLBM=潜射弹道导弹、ICBM=洲际弹道导弹、"
        "Songster=鸣禽、Spriggan=树精、Shkval=暴风、Kluge=笨拙、"
        "Greyhound=灰狗、Hammerhead=锤头、Scimitar=弯刀、"
        "Filin=雕鸮、Coyote=郊狼、Flamingo=火烈鸟、Hurricane=飓风、"
        "Kitchen=厨房、Kingfish=王鱼、Kipper=鲱鱼、"
        "Kelt=鲣鱼、Kangaroo=袋鼠、Kennel=狗窝、"
        "Krypton=氪、Kazoo=卡祖笛、Kedge=小锚、"
        "Kaleidoscope=万花筒、Kamikaze=神风、"
        "Quail=鹌鹑、Goblet=高脚杯、Bandfish=带鱼、"
        "Seeschlange=海蛇、Guideline=导线、Guild=行会、"
        "Scarp=悬崖、Stiletto=匕首、Kitchen=厨房、"
        "Sagger=萨格尔、Swatter=蝇拍、Spiral=螺旋、"
        "Spigot=龙头、Spike=长钉、Snake=蛇、"
        "Ataka=攻击、Vikhr=旋风、Khrizantema=菊花、"
        "Malyutka=婴儿、Fleyta=长笛、Falanga=方阵、"
        "Bass Tilt=低音倾斜、Foxhound=猎狐犬、Foxbat=狐蝠。",
        "厂商音译：OTO Melara=奥托·梅莱拉、Hispano Suiza=西班牙瑞士、"
        "Rheinmetall=莱茵金属、Bofors=博福斯、Nexter=奈克斯特、"
        "Denel=丹尼尔、Roketsan=罗克特桑、IAI=以色列航空航天工业、"
        "Raytheon=雷神、Lockheed=洛克希德、Boeing=波音、"
        "MBDA=欧洲导弹集团、Saab=萨博、Kongsberg=康斯伯格、"
        "Thales=泰雷兹、Leonardo=莱昂纳多、KBP=图拉仪器设计局、"
        "NPO Mashinostroyenia=机械制造科研生产联合体、"
        "Raduga=彩虹设计局、Novator=革新者设计局、"
        "Almaz=金刚石、Antey=安泰、Fakel=火炬、"
        "Vympel=三角旗、Zvezda=星辰、Region=区域、"
        "Bazalt=玄武岩、Tactical Missiles=战术导弹、"
        "Luch=射线、Artem=阿尔乔姆、Belspetsvneshtechnika=白俄对外特种技术、"
        "Zhongguo=中国、Norinco=北方工业、CASC=中国航天科技、"
        "CASIC=中国航天科工、CALT=中国运载火箭技术研究院、"
        "Aviadvigatel=航空发动机、NPO Saturn=土星科研生产联合体、"
        "Klimov=克里莫夫、Isayev=伊萨耶夫、KBM=科洛姆纳机械设计局。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "mount": [
        "把每个 en（武器安装基座/发射装置）译成规范简体中文。型号/编号原样保留。",
        "基座形式：Single Rail=单轨发射架、Twin Turret=双联装炮塔、"
        "Triple=三联装、Quad=四联装、Chaff/Flare=箔条/红外诱饵、"
        "Vehicle=车载、Rail=导轨、Silo=发射井、"
        "TEL=运输起竖发射车、TELAR=运输起竖发射雷达车、"
        "VLS=垂直发射系统、RWS=遥控武器站、CIWS=近程防御武器系统、"
        "MANPADS=便携式防空导弹、Pedestal=基座式发射装置、"
        "Launch Ramp=发射斜架、Launch Rail=发射导轨、"
        "Magazine=弹药库、Cells=单元、Mount=基座、"
        "Turret=炮塔、Gun Mount=炮架、Missile Launcher=导弹发射装置、"
        "Torpedo Tubes=鱼雷发射管、Rocket Launcher=火箭发射装置、"
        "Grenade Launcher=榴弹发射器、Mortar=迫击炮、"
        "Gun=炮、Cannon=机炮、Machine Gun=机枪、"
        "Autocannon=自动炮、CIWS=近防系统、"
        "Vertical Launch=垂直发射、Box Launcher=箱式发射装置、"
        "Rail Launcher=导轨发射装置、Tube Launcher=管式发射装置、"
        "Canister Launcher=箱式发射装置、"
        "Single=单联、Double=双联、Twin=双联装、"
        "Multiple=多联、Cluster=集束、"
        "Internal=内置、External=外置、Conformal=保形、"
        "Wing=翼下、Fuselage=机身、Underwing=翼下、"
        "Nose=机首、Tail=尾部、Dorsal=背部、Ventral=腹部。",
        "雷达/导弹绰号按通行译名（Pat Hand=拍手、Ganef=加涅夫、Krug=克鲁格、"
        "Greyhound=灰狗、Grinch=格林奇、Gimlet=手钻、Spriggan=斯普里根、"
        "Mistral=西北风、Albatros=信天翁、Kornet=短号、Igla=针、"
        "Hwasong=火星、Scud=飞毛腿、Shahab=流星、Fateh=征服者、"
        "Zelzal=地震、Pukguksong=北极星、Lancet=柳叶刀、"
        "Songster=鸣禽、Kluge=笨拙、Hammerhead=锤头、"
        "Filin=雕鸮、Coyote=郊狼、Flamingo=火烈鸟、"
        "Hurricane=飓风、Thunder=雷、Tornado=龙卷风、"
        "Typhoon=台风、Cyclone=旋风、Storm=风暴、"
        "Rapier=长剑、Sea Wolf=海狼、Sea Cat=海猫、"
        "Sea Dart=海标枪、Sea Skua=海鸥、Sea Eagle=海鹰、"
        "Exocet=飞鱼、Otomat=奥托马特、Harpoon=鱼叉、"
        "Penguin=企鹅、AS.12=AS.12、AS.30=AS.30、"
        "Bullpup=小斗犬、Maverick=小牛、Hellfire=地狱火、"
        "Tow=陶式、Dragon=龙、Shillelagh=橡树棍、"
        "Swingfire=旋火、Vigilant=警惕、Milan=米兰、"
        "HOT=热、Spike=长钉、Javelin=标枪、"
        "Panzerfaust=铁拳、RPG=火箭筒、Carl Gustaf=卡尔·古斯塔夫、"
        "AT4=AT4、LAW=轻型反坦克武器、"
        "Stinger=毒刺、Igla=针、Strela=箭、"
        "Blowpipe=吹管、Javelin=标枪、Starstreak=星光、"
        "Mistral=西北风、RBS 70=RBS 70、"
        "Chaparral=小槲树、 Roland=罗兰、Crotale=响尾蛇、"
        "Shahine=沙欣、Shahin=沙欣、"
        "Skyguard=天空卫士、Sky Sabre=天剑、"
        "Land Ceptor=陆地拦截者、CAMM=通用防空模块化导弹、"
        "Aster=紫菀、Barak=巴拉克、"
        "Sky Guard=天空卫士、Contraves=康特拉维斯、"
        "Fire Control=火控、Tracking=跟踪、 "
        "Illuminator=照射雷达、Search=搜索、 "
        "Acquisition=截获、Guidance=制导、 "
        "Command=指令、Beam Riding=驾束制导、"
        "Semi-Active=半主动、Active=主动、"
        "Homing=寻的、Infrared=红外、Laser=激光、"
        "Radar=雷达、TV=电视、Optical=光学、"
        "Wire Guided=有线制导、Fire and Forget=发射后不管。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "propulsion": [
        "把每个 en（推进装置）译成规范简体中文。型号/编号原样保留。",
        "装置：Steam Turbine=蒸汽轮机、Gas Turbine=燃气轮机、Diesel=柴油机、"
        "Boilers=锅炉、Electric=电动、Nuclear Reactor=核反应堆、"
        "Turbo-electric=涡轮电力、Geared=齿轮传动、Jet=喷气、"
        "Turbofan=涡扇、Turbojet=涡喷、Turboprop=涡桨、Turboshaft=涡轴、"
        "Ramjet=冲压、Scramjet=超燃冲压、Waterjets=喷水推进器、"
        "Pumpjet=泵喷推进器、Piston Engine=活塞发动机、"
        "Wankel=汪克尔转子发动机、Azimuthing Thrusters=全回转推进器、"
        "Motor=电动机、Generator=发电机、Battery=电池、"
        "Fuel Cell=燃料电池、Engine=发动机、Turbine=涡轮机、"
        "Compressor=压缩机、Afterburner=加力燃烧室、"
        "Reduction Gear=减速齿轮、Propeller=螺旋桨、"
        "Shaft=轴、Drive=驱动、Thruster=推进器、"
        "Boiler=锅炉、Steam=蒸汽、Gas=燃气、Nuclear=核动力、"
        "Reactor=反应堆、Turbogenerator=涡轮发电机、"
        "Diesel Generator=柴油发电机、Auxiliary=辅助、"
        "Emergency=应急、Main=主、Aux=辅助。",
        "动力形式：CODAG=柴燃联合、CODOG=柴燃交替、COGAG=全燃联合、"
        "CODAD=全柴联合、COGOG=全燃交替、CODLAG=柴电燃联合、"
        "COGLAG=燃电燃联合、CODOGAL=柴燃交替、"
        "Diesel-Electric=柴电、Nuclear=核动力、Conventional=常规动力、"
        "Combined=联合、Integrated=综合、Combined Diesel=柴油机联合、"
        "Combined Gas=燃气轮机联合、"
        "Diesel or Gas=柴油机或燃气轮机、"
        "Steam=蒸汽、Motor=电动机、Electric=电力、"
        "Hybrid=混合、Turbo=涡轮、"
        "Supercharged=增压、Turbocharged=涡轮增压、"
        "Intercooled=中冷、Aftercooled=后冷、"
        "Naturally Aspirated=自然吸气、"
        "Direct Injection=直喷、Common Rail=共轨。",
        "厂商音译：Rolls-Royce=罗尔斯·罗伊斯、"
        "General Electric=通用电气、Babcock=巴布科克、"
        "Wartsila=瓦锡兰、Pielstick=皮尔斯蒂克、"
        "Caterpillar=卡特彼勒、Lycoming=莱康明、"
        "Pratt & Whitney=普惠、Mitsubishi=三菱、"
        "Nakajima=中岛、Perkins=珀金斯、Cummins=康明斯、"
        "Deutz=道依茨、Scania=斯康尼亚、Kongsberg=康斯伯格、"
        "Kamewa=卡米瓦、Isotta Fraschini=伊索塔-弗拉斯基尼、"
        "Bazan=巴桑、Baudouin=博杜安、Tatra=太脱拉、"
        "MAN=曼、MTU=MTU、Solar=索拉、Allison=艾利逊、"
        "Turbomeca=透博梅卡、Klimov=克里莫夫、"
        "Kolomna=科洛姆纳、Zvezda=星辰、Barracuda=梭鱼、"
        "Veliky=伟大、Type=型、Model=型号、Series=系列、"
        "Rotax=罗泰克斯、iS= iS（型号后缀，保留）、"
        "Rotax iS=罗泰克斯 iS、Rotax 914=罗泰克斯 914、"
        "Rotax 915=罗泰克斯 915、Jabiru=鹳、"
        "Sauer=绍尔、Zoche=佐赫、"
        "Austro=奥斯特罗、SMA=SMA、"
        "Continental=大陆、Franklin=富兰克林、"
        "PZL=PZL、Walter=沃尔特、"
        "Motorlet=摩托莱特、"
        "Ivchenko=伊夫琴科、Progress=进步、"
        "Motor Sich=西奇发动机、"
        "Saturn=土星、Aviadvigatel=航空发动机、"
        "NPO Saturn=土星科研生产联合体、"
        "Kuznetsov=库兹涅佐夫、"
        "Type=型、Model=型号、Series=系列、"
        "Generic=通用型、Commercial=商用、"
        "Standard=标准、Heavy=重型、Light=轻型、"
        "Marine=船用、Industrial=工业用、"
        "Aircraft=航空、Helicopter=直升机、"
        "Vehicle=车辆、Tank=坦克、"
        "Outboard=舷外、Inboard=舷内、"
        "Stern=艉部、Bow=艏部、"
        "Azimuth=方位、Rotatable=可旋转、"
        "Fixed=固定、Retractable=可收放、"
        "Controllable Pitch=可调螺距、"
        "Fixed Pitch=固定螺距、"
        "Waterjet=喷水推进、Pump Jet=泵喷、"
        "Cycloidal=摆线、Voith=福伊特、"
        "Schneider=施耐德、"
        "Nuclear=核、Steam=蒸汽、"
        "Pressurized Water=压水堆、"
        "Liquid Metal=液态金属、"
        "Gas Cooled=气冷堆、"
        "Reactor=反应堆、"
        "Propulsion=推进、Power=动力、Plant=装置、"
        "System=系统、Unit=机组、"
        "Main Engine=主机、Auxiliary Engine=辅机、"
        "Generator Set=发电机组、"
        "Emergency Generator=应急发电机、"
        "Service Generator=日用发电机、"
        "Shaft Generator=轴带发电机、"
        "Diesel Generator=柴油发电机、"
        "Turbo Generator=涡轮发电机、"
        "Steam Generator=蒸汽发生器、"
        "Boiler=锅炉、"
        "Auxiliary Boiler=辅助锅炉、"
        "Exhaust Boiler=废气锅炉、"
        "Composite Boiler=组合锅炉、"
        "Waste Heat=废热。",
        "纯型号保留原样；译文完整通顺。",
    ],
    "ship": [
        "把每个 en（舰艇：舷号+舰名+[舰级]）译成规范简体中文。",
        "舷号原样保留（R 21、DDH 205、F 111、L 3029、A 84）。",
        "舰名新华社音译或意译（Melbourne=墨尔本、Victorious=胜利、Reliant=信赖）。",
        "舰级通行译名：Majestic=威严级、Tide=潮汐级、Illustrious=光辉级、"
        "Rothesay=罗思赛级、Leander=利安得级、Perry=佩里级、"
        "Arleigh Burke=阿利·伯克级、Ticonderoga=提康德罗加级、"
        "Nimitz=尼米兹级、Kitty Hawk=小鹰级、Forrestal=福莱斯特级、"
        "Iowa=艾奥瓦级、Los Angeles=洛杉矶级、Ohio=俄亥俄级、"
        "Kilo=基洛级、Oberon=奥伯龙级、Agosta=阿戈斯塔级、"
        "Whiskey=威士忌级、Foxtrot=狐步级、"
        "Type 42=42型、Type 23=23型、Gearing=基林级、"
        "Sumner=萨姆纳级、Fletcher=弗莱彻级、Gleaves=格里夫斯级、"
        "Flower=花级、River=河流级、Castle=城堡级、Tribal=部族级、"
        "Sandown=桑当级、Hunt=狩猎级、Bay=海湾级、Island=岛屿级、"
        "Cyclone=旋风级、Reliance=信赖级、Hamilton=汉密尔顿级、"
        "Pr. 系列按「NN型」（Pr.1155=1155型）、Type 按「NN型」。",
        "通用词：Class=级、Type=型、Batch=批、Flight=批次、"
        "Late=后期型、Early=早期型、Modified=改进型、"
        "FRAM=FRAM 改装、DWT=载重吨、GT=总吨、"
        "RO/RO=滚装船、Container=集装箱船、Tanker=油船、"
        "Carrier=运输舰、Bulk Carrier=散货船、"
        "Cargo=货船、Passenger=客船、Ferry=渡轮、"
        "Fishing=渔船、Tug=拖船、Barge=驳船、"
        "Dredger=挖泥船、Research=调查船、"
        "Survey=测量船、Patrol=巡逻艇、"
        "Fast Attack=快速攻击艇、"
        "Missile Boat=导弹艇、Torpedo Boat=鱼雷艇、"
        "Gunboat=炮艇、Corvette=轻型护卫舰、"
        "Frigate=护卫舰、Destroyer=驱逐舰、"
        "Cruiser=巡洋舰、Battleship=战列舰、"
        "Aircraft Carrier=航空母舰、"
        "Amphibious=两栖、Landing Ship=登陆舰、"
        "Landing Craft=登陆艇、"
        "Submarine=潜艇、Nuclear=核动力、"
        "Conventional=常规动力、"
        "Auxiliary=辅助舰、Tender=供应舰、"
        "Replenishment=补给舰、"
        "Oiler=油船、Ammunition=弹药船、"
        "Hospital=医疗船、Repair=维修船、"
        "Icebreaker=破冰船、"
        "Hydrographic=水道测量船、"
        "Training=训练舰、"
        "Yacht=游艇、"
        "Commercial=商用、Civil=民用、"
        "Merchant=商船、Fishing Vessel=渔船、"
        "Pleasure=游艇、Sailboat=帆船、"
        "Motorboat=摩托艇、Speedboat=快艇、"
        "Life Boat=救生艇、Rescue=救援船、"
        "Fireboat=消防船、Police=警艇、"
        "Customs=海关船、Pilot=引航船、"
        "Lightship=灯船、Buoy Tender=航标船、"
        "Cable Layer=布缆船、"
        "Salvage=打捞船、"
        "Diving Support=潜水支援船、"
        "Seismic=地震勘探船、"
        "Drillship=钻井船、"
        "Platform=平台、"
        "Semi-Submersible=半潜式、"
        "Jack-up=自升式、"
        "Crane=起重船、"
        "Pipelayer=铺管船、"
        "Accommodation=住宿船、"
        "Construction=工程船、"
        "Heavy Lift=重型起重船、"
        "Transport=运输船、"
        "Landing=登陆、Dock=船坞、"
        "Landing Platform=登陆平台、"
        "Landing Helicopter=直升机登陆、"
        "Assault=突击、"
        "Command=指挥舰、"
        "Communications=通信舰、"
        "Relay=中继舰、"
        "Tracking=跟踪舰、"
        "Range=靶场、"
        "Instrumentation=测量舰、"
        "Support=支援舰、"
        "Tender=供应舰。",
        "纯型号保留原样；译文完整通顺。",
    ],
}

TABS = {
    "sensor": ["DataSensor", "DataComm", "DataCommType"],
    "loadout": ["DataLoadout", "DataMagazine", "DataStores", "DataContainer"],
    "facility": ["DataFacility", "DataGroundUnit"],
    "aircraft": ["DataAircraft", "DataSubmarine"],
    "satellite": ["DataSatellite"],
    "weapon": ["DataWeapon", "DataWarhead"],
    "mount": ["DataMount"],
    "propulsion": ["DataPropulsion"],
    "ship": ["DataShip"],
}


def main() -> int:
    gaps = json.loads(GAPS.read_text(encoding="utf-8"))
    db = [g for g in gaps if g["category"] == "db"]
    outroot = ROOT / "build" / "v3_batches"
    outroot.mkdir(parents=True, exist_ok=True)
    for d in outroot.iterdir():
        if d.is_dir():
            for f in d.glob("batch_*.json"):
                f.unlink()

    total = 0
    for key, tabs in TABS.items():
        sel = [g for g in db if g["table"].split(".", 1)[-1] in tabs]
        items, seen = [], set()
        for g in sel:
            if g["src"] in seen:
                continue
            seen.add(g["src"])
            items.append({"en": g["src"], "old": g["dst"]})
        if not items:
            continue
        outdir = outroot / key
        outdir.mkdir(parents=True, exist_ok=True)
        groups = [items[i:i + 150] for i in range(0, len(items), 150)]
        for i, g in enumerate(groups, 1):
            (outdir / f"batch_{i:03d}.json").write_text(
                json.dumps({"batch": i, "instructions": BASE + RULES[key],
                            "entries": g}, ensure_ascii=False, indent=1),
                encoding="utf-8")
        total += len(items)
        print(f"  {key:<12}: {len(items):>4} 条 → {len(groups)} 批")
    print(f"合计 {total} 条")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
