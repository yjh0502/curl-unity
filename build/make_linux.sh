for lib in openssl nghttp2 curl; do
    (cd $lib && ./make_linux.sh)
done

mkdir -p ../Assets/curl-unity/Plugins/linux
cp curl/prebuilt/linux/lib/libcurl.so ../Assets/curl-unity/Plugins/linux