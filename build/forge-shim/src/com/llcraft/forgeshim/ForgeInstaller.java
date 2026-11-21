package com.llcraft.forgeshim;

import java.io.File;
import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.List;

/**
 * Forge / NeoForge 官方安装器的外壳。
 *
 * <p>直接跑 {@code java -jar xxx-installer.jar --installClient <dir>} 有两个问题：
 * 所有依赖都从境外官方源下载，国内网络下很慢且容易中断；
 * 而且会额外下载 mojmaps（几十 MB 的混淆映射表），开发调试才用得到，
 * 启动游戏并不需要。</p>
 *
 * <p>这里用反射调用安装器自己的类，做两件事：换成 BMCLAPI 镜像、
 * 从 processor 列表里剔除 mojmaps。全程反射是因为安装器 jar 是运行时才下载的，
 * 编译期拿不到里面的类。</p>
 *
 * <p><b>必须用 --release 8 编译。</b>启动器会按版本要求挑选 Java，
 * 探测失败时会退回较低版本（实测退到过 Java 8）；
 * 若字节码版本高于运行时的 Java，会直接抛 UnsupportedClassVersionError，
 * 表现为弹一个「A Java Exception has occurred」的错误框，日志里才有原因。
 * 因此这里刻意只用 Java 8 语法与 API。</p>
 *
 * <p>用法：{@code java -cp "forgeshim.jar;installer.jar" com.llcraft.forgeshim.ForgeInstaller <游戏目录> <安装器jar>}</p>
 *
 * <p>输出刻意使用英文：Windows 控制台默认是 GBK，
 * 中文经管道传给启动器时会变成乱码，反而看不清日志。</p>
 */
public final class ForgeInstaller
{
    private static final String MIRROR_NAME = "bmclapi";
    private static final String MIRROR_URL = "http://bmclapi.bangbang93.com/maven/";

    public static void main(String[] args)
    {
        try
        {
            File mcFolder = new File(args.length > 0 ? args[0] : ".");
            File installerJar = new File(args.length > 1 ? args[1] : "");
            System.exit(install(mcFolder, installerJar));
        }
        catch (Throwable t)
        {
            System.out.println("[LLCL] install failed: " + t);
            t.printStackTrace(System.out);
            System.exit(1);
        }
    }

    private static int install(File mcFolder, File installerJar) throws Exception
    {
        Class<?> utilClass = Class.forName("net.minecraftforge.installer.json.Util");
        Method loadProfile = utilClass.getMethod("loadInstallProfile");
        Object install = loadProfile.invoke(null);
        if (install == null)
            throw new IllegalStateException("cannot load install profile");

        System.out.println("[LLCL] install profile loaded");

        applyMirror(install);
        dropMojmaps(install);

        Class<?> callbackClass = Class.forName("net.minecraftforge.installer.actions.ProgressCallback");
        Object monitor = callbackClass.getField("TO_STD_OUT").get(null);

        Class<?> clientInstallClass = Class.forName("net.minecraftforge.installer.actions.ClientInstall");
        Constructor<?> ctor = clientInstallClass.getConstructor(install.getClass(), callbackClass);
        Object clientInstall = ctor.newInstance(install, monitor);

        Method run = clientInstallClass.getMethod("run", File.class, File.class);

        System.out.println("[LLCL] installing into " + mcFolder.getAbsolutePath());

        boolean success;
        try
        {
            success = ((Boolean) run.invoke(clientInstall, mcFolder, installerJar)).booleanValue();
        }
        catch (java.lang.reflect.InvocationTargetException e)
        {
            // 个别版本第二参数含义不同，退化为传目标目录再试一次
            System.out.println("[LLCL] retry with target dir as second argument");
            success = ((Boolean) run.invoke(clientInstall, mcFolder, mcFolder)).booleanValue();
        }

        System.out.println(success ? "[LLCL] install succeeded" : "[LLCL] install reported failure");
        return success ? 0 : 1;
    }

    /** 找到任意一个类型为 Mirror 的字段并塞进去；找不到就保持官方源。 */
    private static void applyMirror(Object install)
    {
        try
        {
            Class<?> mirrorClass = Class.forName("net.minecraftforge.installer.json.Mirror");
            Object mirror = buildMirror(mirrorClass);
            if (mirror == null) return;

            Field field = findFieldOfType(install.getClass(), mirrorClass);
            if (field == null)
            {
                System.out.println("[LLCL] no mirror field found, keep official sources");
                return;
            }

            field.set(install, mirror);
            System.out.println("[LLCL] download mirror switched to " + MIRROR_URL);
        }
        catch (Throwable t)
        {
            System.out.println("[LLCL] mirror switch skipped: " + t);
        }
    }

    /** Mirror 有 (String,String,String,String) 与无参两种构造，按可用性选。 */
    private static Object buildMirror(Class<?> mirrorClass) throws Exception
    {
        try
        {
            Constructor<?> ctor = mirrorClass.getConstructor(
                String.class, String.class, String.class, String.class);
            // 依次为 name / imageAddress / homepage / url
            return ctor.newInstance(MIRROR_NAME, "", "", MIRROR_URL);
        }
        catch (NoSuchMethodException ignored)
        {
            // 继续尝试无参构造
        }

        Object instance = mirrorClass.getConstructor().newInstance();
        setIfPresent(instance, "name", MIRROR_NAME);
        setIfPresent(instance, "url", MIRROR_URL);
        return instance;
    }

    private static void setIfPresent(Object target, String fieldName, String value) throws Exception
    {
        Field field = findField(target.getClass(), fieldName);
        if (field != null) field.set(target, value);
    }

    /** 从处理链里剔除 mojmaps 相关项。 */
    private static void dropMojmaps(Object install)
    {
        try
        {
            Field field = findField(install.getClass(), "processors");
            if (field == null) return;

            Object value = field.get(install);
            if (!(value instanceof List)) return;

            List<?> list = (List<?>) value;
            List<Object> kept = new ArrayList<Object>();
            int dropped = 0;

            for (Object item : list)
            {
                if (item == null) continue;
                if (looksLikeMojmaps(item)) { dropped++; continue; }
                kept.add(item);
            }

            @SuppressWarnings("unchecked")
            List<Object> mutable = (List<Object>) value;
            mutable.clear();
            mutable.addAll(kept);

            System.out.println("[LLCL] dropped " + dropped + " mojmaps processor(s)");
        }
        catch (Throwable t)
        {
            System.out.println("[LLCL] mojmaps filter skipped: " + t);
        }
    }

    private static boolean looksLikeMojmaps(Object processor)
    {
        try
        {
            Method argsMethod = processor.getClass().getMethod("getArgs");
            Object args = argsMethod.invoke(processor);
            if (args instanceof String[])
            {
                for (String a : (String[]) args)
                {
                    if (a != null && a.toUpperCase().contains("MOJMAP")) return true;
                }
            }

            Method jarMethod = processor.getClass().getMethod("getJar");
            Object jar = jarMethod.invoke(processor);
            if (jar != null && jar.toString().toUpperCase().contains("MOJMAP")) return true;
        }
        catch (Throwable ignored)
        {
            // 结构不符就当作普通项
        }

        return false;
    }

    private static Field findField(Class<?> type, String name)
    {
        Class<?> current = type;
        while (current != null && current != Object.class)
        {
            try
            {
                Field field = current.getDeclaredField(name);
                field.setAccessible(true);
                return field;
            }
            catch (NoSuchFieldException e)
            {
                current = current.getSuperclass();
            }
        }
        return null;
    }

    private static Field findFieldOfType(Class<?> type, Class<?> fieldType)
    {
        Class<?> current = type;
        while (current != null && current != Object.class)
        {
            for (Field field : current.getDeclaredFields())
            {
                if (field.getType() == fieldType)
                {
                    field.setAccessible(true);
                    return field;
                }
            }
            current = current.getSuperclass();
        }
        return null;
    }
}

