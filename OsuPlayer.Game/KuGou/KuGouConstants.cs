namespace OsuPlayer.Game.KuGou
{
    /// <summary>
    /// KuGou client flavour. The "lite" platform is the 酷狗概念版 client this player is modelled after.
    /// </summary>
    public enum KuGouPlatform
    {
        Standard,
        Lite
    }

    /// <summary>
    /// Hard-coded constants shipped inside the official KuGou Android clients and used for request signing.
    /// The two sets below are exactly KuGouMusicApi's <c>util/config.json</c> /
    /// <c>util/helper.js</c> pairs: 标准版 (appid 1005) and 概念版/lite (appid 3116) each have their own
    /// appid, clientver and MD5 salts, and login tokens are NOT interchangeable between them.
    /// </summary>
    internal static class KuGouConstants
    {
        public const int srcappid = 2919;

        public const int standard_appid = 1005;
        public const int standard_clientver = 20489;

        public const int lite_appid = 3116;
        public const int lite_clientver = 11440;

        /// <summary>Android request signature salt (standard client).</summary>
        public const string android_salt = "OIlwieks28dk2k092lksi2UIkp";

        /// <summary>Android request signature salt (概念版/lite client).</summary>
        public const string android_lite_salt = "LnT6xpN3khm36zse0QzvmgTZ3waWdRSA";

        /// <summary>Web/H5 request signature salt.</summary>
        public const string web_salt = "NVPh5oo715z5DIWAeQlhMDsWXXQV4hwt";

        /// <summary>signKey salt, standard client.</summary>
        public const string sign_key_salt = "57ae12eb6890223e355ccfcb74edf70d";

        /// <summary>signKey salt, lite client.</summary>
        public const string sign_key_lite_salt = "185672dd44712f60bb1736df5a377e82";

        public const string gateway_base_url = "https://gateway.kugou.com";

        /// <summary>Some newer gateway routes (home discover) only answer on plain HTTP.</summary>
        public const string gateway_http_base_url = "http://gateway.kugou.com";

        public const string mobilecdn_base_url = "http://mobilecdn.kugou.com";
        public const string lyrics_base_url = "http://lyrics.kugou.com";
        public const string login_base_url = "http://login-user.kugou.com";
        public const string songlist_base_url = "https://t.kugou.com";

        /// <summary>概念版「免费听」授权服务（KuGouMusicApi/module/song_auth.js 的 /v1/authorization）。</summary>
        public const string tracker_auth_base_url = "http://trackercdngz.kugou.com";

        /// <summary>会员产品查询服务（module/user_vip_detail.js 的 /v1/get_union_vip）。</summary>
        public const string vip_base_url = "https://kugouvip.kugou.com";

        /// <summary>账号资料服务（module/user_info.js 的 /v1/get_my_userinfo）。</summary>
        public const string relation_base_url = "http://relation.user.kugou.com";

        /// <summary>
        /// 概念版免费听/授权链路专用的客户端版本与模块号（song_auth.js、song_url_auth.js 的固定取值，
        /// 与平台无关）。
        /// </summary>
        public const int auth_clientver = 11561;
        public const int auth_module_id = 51;

        // -------- 安全验证（SSA / 人机校验） --------
        // 网关对高风险请求返回 errcode 20028 + ssa-code 响应头，要求完成人机验证后才能继续。
        // 流程对齐 KuGouMusicApi：/verifyservice/v3/get_verify_info 取验证配置 →
        // 浏览器完成腾讯验证码 → /v4/verify_user_info 提交（sid/edt 为服务端模拟的行为指纹）。

        /// <summary>验证服务域名（verify_user_info.js）。</summary>
        public const string verifyservice_base_url = "https://verifyservice.kugou.com";

        /// <summary>验证接口的查询参数 clientver（verify_user_info.js 固定 11510）。</summary>
        public const int verify_clientver = 11510;

        /// <summary>腾讯验证码 SDK 脚本与提交前缀（KugouVerificationFlow.vue 的 KGCodeTX|...）。</summary>
        public const string tcaptcha_script_url = "https://turing.captcha.qcloud.com/TCaptcha.js";
        public const string tcaptcha_code_prefix = "KGCodeTX";

        /// <summary>行为指纹 AES 固定 IV（generate_simulate.js：ASCII "kugousecurity123"）。</summary>
        public const string simulate_iv = "kugousecurity123";

        /// <summary>RSA 公钥，用于 encrypt AES 密钥 / 上报验证（util/crypto.js）。</summary>
        public const string public_rsa_key =
            "-----BEGIN PUBLIC KEY-----\n"
            + "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDIAG7QOELSYoIJvTFJhMpe1s/gbjDJX51HBNnEl5HXqTW6lQ7LC8jr9fWZTwusknp+sVGzwd40MwP6U5yDE27M/X1+UR4tvOGOqp94TJtQ1EPnWGWXngpeIW5GxoQGao1rmYWAu6oi1z9XkChrsUdC6DJE5E221wf/4WLFxwAtRQIDAQAB\n"
            + "-----END PUBLIC KEY-----";

        public const string public_lite_rsa_key =
            "-----BEGIN PUBLIC KEY-----\n"
            + "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDECi0Np2UR87scwrvTr72L6oO01rBbbBPriSDFPxr3Z5syug0O24QyQO8bg27+0+4kBzTBTBOZ/WWU0WryL1JSXRTXLgFVxtzIY41Pe7lPOgsfTCn5kZcvKhYKJesKnnJDNr5/abvTGf+rHG3YRwsCHcQ08/q6ifSioBszvb3QiwIDAQAB\n"
            + "-----END PUBLIC KEY-----";

        /// <summary>行为指纹 RSA-OAEP 公钥（generate_simulate.js，2048-bit）。</summary>
        public const string simulate_rsa_key =
            "-----BEGIN PUBLIC KEY-----\n"
            + "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAoW2+Ylo8ALePSQTP0xBF\n"
            + "lFmEOHvBD9tS+s7DBlfKEu3RzzvZTaX1JtYbX4+AVUqj6ARz8IM+CKByqGFvbHN/\n"
            + "W64XxNI+q7z36ajCL3VTJ2W5G9MCJitc6oGbire4NQfhaEq0nC+hxBWQvCbIFflA\n"
            + "2ItrLUbSU7z1bHA/a+jlQm4OWvY+IKnTryOJTPuT1yNOVjbJ8wBLKy2DgQr9pPqW\n"
            + "PmEQtGpR5IM9V8Kao6PaSdKYOWGbX3i2+RzIKhvZUxxtJwdVbqPlDPlW9h4/xIBc\n"
            + "56Lgvr4aIl8nFtwbj4UJVUTFuGrs0tY9H/tXvZ22dUCKuGxW/gW7ZF+gXz6vHtYa\n"
            + "rQIDAQAB\n"
            + "-----END PUBLIC KEY-----";

        public static string VerifyRsaKey(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? public_lite_rsa_key : public_rsa_key;

        public const string user_agent = "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi";

        public const string browser_user_agent =
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        public static int AppId(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? lite_appid : standard_appid;

        public static int ClientVer(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? lite_clientver : standard_clientver;

        public static string AndroidSalt(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? android_lite_salt : android_salt;

        public static string SignKeySalt(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? sign_key_lite_salt : sign_key_salt;

        // -------- /v5/url 的客户端参数 --------
        // 概念版与标准版取值不同（对齐 KuGouMusicApi/module/song_url.js）：pid/page_id/ppage_id 决定
        // 网关按哪个客户端的权益下发播放地址。

        public static int Pid(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? 411 : 2;

        public static long PageId(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? 967177915 : 151369488;

        public static string PPageId(KuGouPlatform platform) => platform == KuGouPlatform.Lite
            ? "356753938,823673182,967485191"
            : "463467626,350369493,788954147";

        public static string DisplayName(KuGouPlatform platform) =>
            platform == KuGouPlatform.Lite ? "概念版（lite）" : "标准版";
    }
}
