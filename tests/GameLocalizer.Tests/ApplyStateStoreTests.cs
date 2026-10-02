using GameLocalizer.Infrastructure.FileSystem;
using Xunit;
namespace GameLocalizer.Tests;
public sealed class ApplyStateStoreTests
{
    [Fact] public async Task AppliedPersistsAndChangedFileInvalidatesVerification()
    {
        var root=Path.Combine(Path.GetTempPath(),"GameLocalizerApplyState",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var file=Path.Combine(root,"dialogue.txt");await File.WriteAllTextAsync(file,"Перевод");
            var result=new GameApplyResult {Root=root,State=GameApplyState.Applied,VerifiedFiles=new(){{"dialogue.txt",TextFiles.Hash(await File.ReadAllBytesAsync(file))}}};
            await new ApplyStateStore(Path.Combine(root,"states")).SaveAsync(result);
            var restored=Assert.IsType<GameApplyResult>(new ApplyStateStore(Path.Combine(root,"states")).Load(root));
            Assert.Equal(GameApplyState.Applied,restored.State);Assert.True(await ApplyStateStore.VerifyFilesAsync(restored,default));
            await File.WriteAllTextAsync(file,"External edit");Assert.False(await ApplyStateStore.VerifyFilesAsync(restored,default));
            result.State=GameApplyState.Applying;await new ApplyStateStore(Path.Combine(root,"states")).SaveAsync(result);
            Assert.Equal(GameApplyState.ApplyFailed,new ApplyStateStore(Path.Combine(root,"states")).Load(root)!.State);
        } finally {Directory.Delete(root,true);}
    }
}
